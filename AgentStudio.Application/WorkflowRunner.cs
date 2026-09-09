using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentStudio.Domain;

namespace AgentStudio.Application;

/// <summary>Emits a chunk of assistant-visible text. The top-level run writes it straight to the
/// live output channel; a parallel branch instead buffers it so concurrent branches' output
/// never interleaves — the owning ParallelNode flushes each branch's buffer, in declared edge
/// order, once all branches have finished. Top-level (not nested in WorkflowRunner) so
/// NodeExecutors.cs's NodeExecutionContext can reference it too.</summary>
internal delegate Task ChunkSink(string text, CancellationToken ct);

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
    private readonly IEnumerable<IIntegrator> _integrators;
    private readonly IAgentCollectionStore _collections;

    /// <summary>"Leaf" node executors (see NodeExecutors.cs) keyed by the WorkflowNode subclass
    /// they handle — built once from this runner's own injected services, not DI-registered
    /// separately, so the constructor below stays exactly as every existing caller/test already
    /// expects. Covers every node whose next edge is always the single unconditional one
    /// (NextByEdge with no branch); Start/End/Condition/Parallel/Join/SubAgent stay in
    /// RunSegmentAsync's switch since their control flow isn't uniform like that.</summary>
    private readonly Dictionary<Type, INodeExecutor> _executorsByType;

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
        IDatabaseQueryExecutor databaseQuery,
        IEnumerable<IIntegrator> integrators,
        IAgentCollectionStore collections)
    {
        _chatClients = chatClients;
        _http = http;
        _logWriter = logWriter;
        _documentSearch = documentSearch;
        _agents = agents;
        _providers = providers;
        _databaseQuery = databaseQuery;
        _integrators = integrators;
        _collections = collections;

        _executorsByType = new List<INodeExecutor>
        {
            new HttpNodeExecutor(_http),
            new DocumentSearchNodeExecutor(_documentSearch, _providers),
            new DatabaseQueryNodeExecutor(_databaseQuery),
            new JsonParseNodeExecutor(),
            new ExpressionNodeExecutor(),
            new CollectionGetNodeExecutor(_collections),
            new CollectionSetNodeExecutor(_collections),
            new IntegratorNodeExecutor(_integrators),
            new PromptNodeExecutor(_chatClients, _providers),
        }.ToDictionary(e => e.NodeType);
    }

    public string? LastExecutionId { get; private set; }

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
        int callDepth = 0,
        IReadOnlyDictionary<string, string>? formValues = null)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var runTask = Task.Run(() => ExecuteAsync(agent, version, provider, conversation, userMessage, channel.Writer, callDepth, ct, formValues), ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            yield return chunk;

        await runTask; // propagate unexpected errors
    }

    /// <summary>Executes a single node in isolation against caller-supplied sample variables —
    /// lets the graph editor test one node (a jsonParse Path, a SQL query, a prompt) without
    /// publishing the whole agent. Runs against a synthetic one-node graph with no outgoing
    /// edges, so it stops after exactly that node regardless of its own branching (a condition's
    /// true/false result still shows up in Detail, it just never continues to a next node).
    /// Never persisted: StartStep/CompleteStep are in-memory only (ExecutionLogWriter), and
    /// CompleteAsync is deliberately never called here, so debug runs don't pollute
    /// /analytics or the agent's execution log.</summary>
    public async Task<NodeDebugResult> DebugNodeAsync(
        Agent agent,
        ModelProviderConfig provider,
        WorkflowGraph graph,
        string nodeId,
        IReadOnlyDictionary<string, string> sampleVariables,
        CancellationToken ct = default)
    {
        var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"Node '{nodeId}' not found in graph.");
        if (node is StartNode or ParallelNode or JoinNode)
            throw new InvalidOperationException($"'{node.Type}' nodes can't be debugged in isolation — run the full graph instead.");

        var variables = new Dictionary<string, string>();
        foreach (var (name, value) in agent.EnvironmentVariables)
            variables[$"env.{name}"] = value;
        foreach (var (name, value) in sampleVariables)
            variables[name] = value;
        var conversation = new ConversationState { ConversationId = $"debug-{Guid.NewGuid():N}", AgentId = agent.Id, AgentVersion = 0 };
        var log = _logWriter.Start(conversation.ConversationId, agent.Id, 0);
        var isolated = new WorkflowGraph();
        isolated.Nodes.Add(node);
        var buffer = new StringBuilder();
        ChunkSink emit = (text, _) => { buffer.Append(text); return Task.CompletedTask; };

        try
        {
            await RunSegmentAsync(isolated, node, stopAtNodeId: null, variables, conversation, agent, provider, log, new StepCounter(), maxSteps: 1, emit, callDepth: 0, ct);
            return new NodeDebugResult(true, log.Steps.LastOrDefault()?.Detail, buffer.ToString(), variables, null);
        }
        catch (Exception ex)
        {
            return new NodeDebugResult(false, log.Steps.LastOrDefault()?.Detail, buffer.ToString(), variables, ex.Message);
        }
    }

    private async Task ExecuteAsync(
        Agent agent,
        AgentVersion version,
        ModelProviderConfig provider,
        ConversationState conversation,
        string userMessage,
        ChannelWriter<string> output,
        int callDepth,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? formValues = null)
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
            // Lowest precedence, seeded first — agent.EnvironmentVariables are admin-configured
            // defaults (phase 12), reachable as {variables.env.NAME}; anything more specific
            // (userMessage/formValues below) is free to use the same name and win.
            foreach (var (name, value) in agent.EnvironmentVariables)
                variables[$"env.{name}"] = value;
            variables["input"] = userMessage;
            // formValues applied AFTER userMessage: a form run always passes userMessage=""
            // (RunForm.razor), so a named field must win — including one literally named
            // "input" (a reasonable choice, since {input} is the documented placeholder).
            if (formValues is not null)
                foreach (var (name, value) in formValues)
                    variables[name] = value;

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
    /// <summary>The StartStep/try-work/CompleteStep(detail)/catch-FailStep-rethrow skeleton every
    /// node case in RunSegmentAsync repeats verbatim — <paramref name="work"/> does the node's
    /// own work and returns the detail string to log (or null for none). Deliberately not used
    /// for the handful of cases (End/Message/Variable/Condition/Join) that don't fail this way —
    /// forcing them in would either change behavior (they don't call FailStep today) or need
    /// special-casing that defeats the point.</summary>
    private async Task RunStepAsync(ExecutionLog log, WorkflowNode node, Func<Task<string?>> work)
    {
        var step = _logWriter.StartStep(log, node.Id, node.Type);
        try
        {
            var detail = await work();
            _logWriter.CompleteStep(step, detail);
        }
        catch (Exception ex)
        {
            _logWriter.FailStep(step, ex.Message);
            throw;
        }
    }

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
                    variables[message.ResultVariable] = text;
                    _logWriter.CompleteStep(step, $"{message.ResultVariable} set");
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
                case SubAgentNode sub:
                {
                    await RunStepAsync(log, sub, async () =>
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
                        return $"subAgent {targetAgent.Name}: {buffer.Length} chars";
                    });
                    current = NextByEdge(graph, sub.Id, branch: null);
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
                {
                    if (!_executorsByType.TryGetValue(current.GetType(), out var executor))
                        throw new InvalidOperationException($"Unsupported node type: {current.Type}");
                    var node = current;
                    var context = new NodeExecutionContext(variables, conversation, agent, provider, ct);
                    await RunStepAsync(log, node, () => executor.ExecuteAsync(node, context));
                    current = NextByEdge(graph, node.Id, branch: null);
                    break;
                }
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

    /// <summary>internal (not private) — HttpNodeExecutor (NodeExecutors.cs) calls this too.</summary>
    internal static HttpNode Expand(HttpNode node, IReadOnlyDictionary<string, string> variables) => new()
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

/// <summary>Result of <see cref="WorkflowRunner.DebugNodeAsync"/> — <c>Detail</c> is the same
/// short status string the real run's execution log would show for this step (e.g. "condition:
/// True", "databaseQuery test-data: 84 chars"); <c>Variables</c> is the full variable set after
/// the node ran, so the caller can see exactly which variable it wrote.</summary>
public sealed record NodeDebugResult(bool Success, string? Detail, string EmittedText, IReadOnlyDictionary<string, string> Variables, string? Error);
