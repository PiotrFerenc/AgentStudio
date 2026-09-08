using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentStudio.Domain;

namespace AgentStudio.Application;

/// <summary>
/// Executes a workflow graph from Start, streaming LLM/message output.
/// Phase 1: sequential + if/else branching. Phase 2 adds loops (cycles + a per-version step
/// cap) and parallel fan-out/fan-in (ParallelNode/JoinNode), each branch running concurrently
/// with its own variable snapshot merged back at the join.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly IChatClientFactory _chatClients;
    private readonly ISecureHttpExecutor _http;
    private readonly IExecutionLogWriter _logWriter;
    private readonly IDocumentSearchService _documentSearch;
    private readonly IAgentRepository _agents;
    private readonly IProviderRepository _providers;
    private readonly IDatabaseQueryExecutor _databaseQuery;

    /// <summary>Hard cap on agent-calling-agent nesting (phase 3, SubAgentNode) — the only guard
    /// against a cycle across agents (A calls B calls A...), since that can't be seen by the
    /// single-graph WorkflowValidator. Not per-agent configurable — YAGNI until someone needs it.</summary>
    private const int MaxSubAgentDepth = 5;

    public WorkflowRunner(
        IChatClientFactory chatClients,
        ISecureHttpExecutor http,
        IExecutionLogWriter logWriter,
        IDocumentSearchService documentSearch,
        IAgentRepository agents,
        IProviderRepository providers,
        IDatabaseQueryExecutor databaseQuery)
    {
        _chatClients = chatClients;
        _http = http;
        _logWriter = logWriter;
        _documentSearch = documentSearch;
        _agents = agents;
        _providers = providers;
        _databaseQuery = databaseQuery;
    }

    public string? LastExecutionId { get; private set; }

    /// <summary>Emits a chunk of assistant-visible text. The top-level run writes it straight to
    /// the live output channel; a parallel branch instead buffers it so concurrent branches'
    /// output never interleaves — the owning ParallelNode flushes each branch's buffer, in
    /// declared edge order, once all branches have finished.</summary>
    private delegate Task ChunkSink(string text, CancellationToken ct);

    private sealed class StepCounter
    {
        private int _value;
        public int Increment() => Interlocked.Increment(ref _value);
    }

    public async IAsyncEnumerable<string> RunAsync(
        Agent agent,
        AgentVersion version,
        ModelProviderConfig provider,
        ConversationState conversation,
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        int callDepth = 0)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var runTask = Task.Run(() => ExecuteAsync(agent, version, provider, conversation, userMessage, channel.Writer, callDepth, ct), ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            yield return chunk;

        await runTask; // propagate unexpected errors
    }

    private async Task ExecuteAsync(
        Agent agent,
        AgentVersion version,
        ModelProviderConfig provider,
        ConversationState conversation,
        string userMessage,
        ChannelWriter<string> output,
        int callDepth,
        CancellationToken ct)
    {
        // Everything — including graph deserialization — must run inside this try/finally.
        // output.Complete() has to fire no matter what fails, or RunAsync's `await foreach`
        // over the channel reader hangs forever waiting for a completion that never comes.
        var reply = new StringBuilder();
        string? error = null;
        ExecutionLog? log = null;

        try
        {
            var graph = version.Graph;
            log = _logWriter.Start(conversation.ConversationId, agent.Id, version.Version);
            // LastExecutionId is instance state on this same WorkflowRunner, reused recursively
            // for sub-agent calls (phase 3) and concurrently across parallel branches (phase 2)
            // — only the outermost call's id is what external callers (Program.cs) ever read
            // (right after their own top-level RunAsync completes), so nested calls must not
            // touch it: skips a save/restore dance that isn't even race-free across concurrent
            // parallel branches each running their own sub-agent call.
            if (callDepth == 0)
                LastExecutionId = log.ExecutionId;
            var variables = conversation.Variables;
            variables["input"] = userMessage;

            lock (conversation)
            {
                conversation.Messages.Add(new ChatMessage { Role = "user", Content = userMessage });
                if (!string.IsNullOrWhiteSpace(agent.SystemInstructions) &&
                    conversation.Messages.All(m => m.Role != "system"))
                    conversation.Messages.Insert(0, new ChatMessage { Role = "system", Content = agent.SystemInstructions });
            }

            ChunkSink emit = async (text, chunkCt) =>
            {
                if (text.Length == 0) return;
                reply.Append(text);
                await output.WriteAsync(text, chunkCt);
            };

            var current = NextByEdge(graph, StartNodeId(graph), branch: null);
            var steps = new StepCounter();

            await RunSegmentAsync(graph, current, stopAtNodeId: null, variables, conversation, agent, provider, log, steps, version.MaxSteps, emit, callDepth, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { await output.WriteAsync($"\n[error] {ex.Message}", CancellationToken.None); }
            catch { /* channel already faulted/completed — nothing left to report to */ }
        }
        finally
        {
            var final = reply.ToString();
            if (final.Length > 0)
                lock (conversation)
                {
                    if (conversation.Messages.Count == 0 || conversation.Messages[^1].Content != final)
                        conversation.Messages.Add(new ChatMessage { Role = "assistant", Content = final });
                }

            if (log is not null)
            {
                try { await _logWriter.CompleteAsync(log, error, CancellationToken.None); }
                catch { /* logging failure must not break the run */ }
            }

            output.Complete();
        }
    }

    /// <summary>Runs nodes starting at <paramref name="current"/> until it naturally ends (End
    /// node / no outgoing edge) or reaches <paramref name="stopAtNodeId"/> — the latter is how a
    /// parallel branch stops exactly at its Join node without executing it (the owning
    /// ParallelNode executes/logs the join once, after merging all branches).</summary>
    private async Task RunSegmentAsync(
        WorkflowGraph graph,
        WorkflowNode? current,
        string? stopAtNodeId,
        Dictionary<string, string> variables,
        ConversationState conversation,
        Agent agent,
        ModelProviderConfig provider,
        ExecutionLog log,
        StepCounter steps,
        int maxSteps,
        ChunkSink emit,
        int callDepth,
        CancellationToken ct)
    {
        while (current is not null && current.Id != stopAtNodeId)
        {
            ct.ThrowIfCancellationRequested();
            if (steps.Increment() > maxSteps)
                throw new InvalidOperationException($"Workflow exceeded maximum step count ({maxSteps}).");

            switch (current)
            {
                case EndNode end:
                {
                    var step = _logWriter.StartStep(log, end.Id, end.Type);
                    if (!string.IsNullOrWhiteSpace(end.OutputTemplate))
                        await emit(ExpandTemplate(end.OutputTemplate, variables), ct);
                    _logWriter.CompleteStep(step);
                    current = null;
                    break;
                }
                case MessageNode message:
                {
                    var step = _logWriter.StartStep(log, message.Id, message.Type);
                    var text = ExpandTemplate(message.Text, variables);
                    _logWriter.CompleteStep(step);
                    await emit(text, ct);
                    current = NextByEdge(graph, message.Id, branch: null);
                    break;
                }
                case VariableNode variable:
                {
                    var step = _logWriter.StartStep(log, variable.Id, variable.Type);
                    variables[variable.Name] = EvaluateVariableValue(variable.Value, variables);
                    _logWriter.CompleteStep(step, $"{variable.Name} set");
                    current = NextByEdge(graph, variable.Id, branch: null);
                    break;
                }
                case ConditionNode condition:
                {
                    var step = _logWriter.StartStep(log, condition.Id, condition.Type);
                    var result = ConditionEvaluator.Evaluate(condition, name => variables.TryGetValue(name, out var v) ? v : null);
                    _logWriter.CompleteStep(step, $"condition: {result}");
                    current = NextByEdge(graph, condition.Id, result ? "true" : "false");
                    break;
                }
                case HttpNode http:
                {
                    var step = _logWriter.StartStep(log, http.Id, http.Type);
                    try
                    {
                        var result = await _http.ExecuteAsync(Expand(http, variables), variables, ct);
                        variables[http.ResultVariable] = result;
                        _logWriter.CompleteStep(step, $"http {http.Method} {http.Url}: {result.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        _logWriter.FailStep(step, ex.Message);
                        throw;
                    }
                    current = NextByEdge(graph, http.Id, branch: null);
                    break;
                }
                case DocumentSearchNode search:
                {
                    var step = _logWriter.StartStep(log, search.Id, search.Type);
                    try
                    {
                        var query = ExpandTemplate(search.Query, variables);
                        var chunks = await _documentSearch.SearchAsync(agent.Id, query, search.TopK, provider, ct);
                        variables[search.ResultVariable] = string.Join("\n\n", chunks);
                        _logWriter.CompleteStep(step, $"documentSearch: {chunks.Count} chunks");
                    }
                    catch (Exception ex)
                    {
                        _logWriter.FailStep(step, ex.Message);
                        throw;
                    }
                    current = NextByEdge(graph, search.Id, branch: null);
                    break;
                }
                case DatabaseQueryNode query:
                {
                    var step = _logWriter.StartStep(log, query.Id, query.Type);
                    try
                    {
                        var result = await _databaseQuery.ExecuteAsync(query, variables, ct);
                        variables[query.ResultVariable] = result;
                        _logWriter.CompleteStep(step, $"databaseQuery {query.ConnectionName}: {result.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        _logWriter.FailStep(step, ex.Message);
                        throw;
                    }
                    current = NextByEdge(graph, query.Id, branch: null);
                    break;
                }
                case SubAgentNode sub:
                {
                    var step = _logWriter.StartStep(log, sub.Id, sub.Type);
                    try
                    {
                        if (callDepth >= MaxSubAgentDepth)
                            throw new InvalidOperationException($"Sub-agent call depth exceeded ({MaxSubAgentDepth}) — likely a cycle between agents.");

                        var targetAgent = await _agents.GetAsync(sub.TargetAgentId, ct)
                            ?? throw new InvalidOperationException($"Sub-agent {sub.TargetAgentId} not found.");
                        var targetVersion = targetAgent.Versions
                            .Where(v => v.Status == AgentVersionStatus.Published)
                            .OrderByDescending(v => v.Version)
                            .FirstOrDefault()
                            ?? throw new InvalidOperationException($"Sub-agent '{targetAgent.Name}' has no published version.");
                        var targetProvider = await _providers.GetByNameAsync(targetAgent.ModelProviderName, ct)
                            ?? throw new InvalidOperationException($"Sub-agent '{targetAgent.Name}': provider '{targetAgent.ModelProviderName}' is not configured.");

                        var subInput = ExpandTemplate(sub.InputTemplate, variables);
                        // A fresh, throwaway conversation — one-shot call, not persisted via
                        // IConversationStore, doesn't share the caller's history.
                        var subConversation = new ConversationState
                        {
                            ConversationId = $"subagent-{Guid.NewGuid():N}",
                            AgentId = targetAgent.Id,
                            AgentVersion = targetVersion.Version
                        };

                        var buffer = new StringBuilder();
                        await foreach (var chunk in RunAsync(targetAgent, targetVersion, targetProvider, subConversation, subInput, ct, callDepth + 1))
                            buffer.Append(chunk);

                        variables[sub.ResultVariable] = buffer.ToString();
                        _logWriter.CompleteStep(step, $"subAgent {targetAgent.Name}: {buffer.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        _logWriter.FailStep(step, ex.Message);
                        throw;
                    }
                    current = NextByEdge(graph, sub.Id, branch: null);
                    break;
                }
                case PromptNode prompt:
                {
                    var step = _logWriter.StartStep(log, prompt.Id, prompt.Type);
                    var buffer = new StringBuilder();
                    try
                    {
                        var promptText = ExpandTemplate(prompt.PromptTemplate, variables);
                        List<ChatMessage> messages;
                        lock (conversation) messages = new List<ChatMessage>(conversation.Messages);
                        if (!string.IsNullOrWhiteSpace(promptText))
                            messages.Add(new ChatMessage { Role = "user", Content = promptText });

                        var client = _chatClients.Create(provider, agent.ModelName);
                        await foreach (var chunk in client.StreamReplyAsync(messages, ct))
                        {
                            buffer.Append(chunk);
                            await emit(chunk, ct);
                        }

                        variables[prompt.ResultVariable] = buffer.ToString();
                        lock (conversation) conversation.Messages.Add(new ChatMessage { Role = "assistant", Content = buffer.ToString() });
                        _logWriter.CompleteStep(step, $"llm: {buffer.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        _logWriter.FailStep(step, ex.Message);
                        throw;
                    }
                    current = NextByEdge(graph, prompt.Id, branch: null);
                    break;
                }
                case ParallelNode split:
                {
                    var step = _logWriter.StartStep(log, split.Id, split.Type);
                    var branchEdges = graph.Edges.Where(e => e.SourceNodeId == split.Id).ToList();
                    var joinId = FindJoinNodeId(graph, split);

                    var branchTasks = branchEdges.Select(async edge =>
                    {
                        var branchVars = new Dictionary<string, string>(variables);
                        var buffer = new StringBuilder();
                        ChunkSink bufferedEmit = (text, _) => { buffer.Append(text); return Task.CompletedTask; };
                        var branchStart = graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
                        await RunSegmentAsync(graph, branchStart, joinId, branchVars, conversation, agent, provider, log, steps, maxSteps, bufferedEmit, callDepth, ct);
                        return (Vars: branchVars, Text: buffer.ToString());
                    }).ToList();

                    var results = await Task.WhenAll(branchTasks);

                    // Merge in declared edge order — later branches win on a key conflict.
                    foreach (var r in results)
                        foreach (var (key, value) in r.Vars)
                            variables[key] = value;

                    _logWriter.CompleteStep(step, $"parallel: {results.Length} branches joined");

                    // Flush each branch's buffered output in that same order — never interleaved.
                    foreach (var r in results)
                        if (r.Text.Length > 0)
                            await emit(r.Text, ct);

                    current = graph.Nodes.FirstOrDefault(n => n.Id == joinId);
                    break;
                }
                case JoinNode join:
                {
                    // Reached directly rather than stopped-at-by the owning ParallelNode — e.g. a
                    // malformed/unvalidated draft during test-chat. Just pass through.
                    var step = _logWriter.StartStep(log, join.Id, join.Type);
                    _logWriter.CompleteStep(step);
                    current = NextByEdge(graph, join.Id, branch: null);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported node type: {current.Type}");
            }
        }
    }

    private static string FindJoinNodeId(WorkflowGraph graph, WorkflowNode parallelNode)
    {
        foreach (var edge in graph.Edges.Where(e => e.SourceNodeId == parallelNode.Id))
        {
            var found = FindFirstJoinForward(graph, edge.TargetNodeId);
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"Parallel node {parallelNode.Id} has no reachable Join node.");
    }

    private static string? FindFirstJoinForward(WorkflowGraph graph, string nodeId)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(nodeId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            var node = graph.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is JoinNode) return node.Id;
            if (node is null) continue;
            foreach (var e in graph.Edges.Where(e => e.SourceNodeId == id))
                queue.Enqueue(e.TargetNodeId);
        }
        return null;
    }

    private static string StartNodeId(WorkflowGraph graph) =>
        graph.Nodes.OfType<StartNode>().FirstOrDefault()?.Id
        ?? throw new InvalidOperationException("Workflow has no Start node.");

    private static HttpNode Expand(HttpNode node, IReadOnlyDictionary<string, string> variables) => new()
    {
        Id = node.Id,
        Method = node.Method,
        Url = ExpandTemplate(node.Url, variables),
        Body = node.Body is null ? null : ExpandTemplate(node.Body, variables),
        TimeoutSeconds = node.TimeoutSeconds,
        Retries = node.Retries,
        ResultVariable = node.ResultVariable,
        Headers = node.Headers.ToDictionary(kv => kv.Key, kv => ExpandTemplate(kv.Value, variables)),
        QueryParameters = node.QueryParameters.ToDictionary(kv => kv.Key, kv => ExpandTemplate(kv.Value, variables))
    };

    private static WorkflowNode? NextByEdge(WorkflowGraph graph, string sourceId, string? branch)
    {
        var edge = graph.Edges.FirstOrDefault(e =>
            e.SourceNodeId == sourceId &&
            (branch is null ? e.Branch is null : e.Branch == branch));
        if (edge is null) return null;
        return graph.Nodes.FirstOrDefault(n => n.Id == edge.TargetNodeId);
    }

    private static readonly Regex Placeholder = new(@"\{(variables\.(?<var>[\w.]+)|input)\}", RegexOptions.Compiled);

    // Whole-template "{variables.name+N}" / "{variables.name-N}" — a minimal increment/decrement
    // primitive (phase 2, loops). Only VariableNode.Value uses this; every other template
    // (Message/End/Http) is plain text substitution via ExpandTemplate. No general arithmetic —
    // this is the smallest addition that makes a bounded loop counter expressible without a
    // dedicated arbitrary-expression evaluator.
    private static readonly Regex ArithmeticAssign = new(
        @"^\{variables\.(?<var>[\w.]+)\s*(?<op>[+-])\s*(?<delta>\d+(\.\d+)?)\}$", RegexOptions.Compiled);

    public static string EvaluateVariableValue(string template, IReadOnlyDictionary<string, string> variables)
    {
        var match = ArithmeticAssign.Match(template.Trim());
        if (!match.Success)
            return ExpandTemplate(template, variables);

        var current = variables.TryGetValue(match.Groups["var"].Value, out var v) &&
            double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        var delta = double.Parse(match.Groups["delta"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var result = match.Groups["op"].Value == "+" ? current + delta : current - delta;
        return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Placeholder.Replace(template, match =>
        {
            var varName = match.Groups["var"].Success ? match.Groups["var"].Value : "input";
            return variables.TryGetValue(varName, out var v) ? v : match.Value;
        });
    }
}
