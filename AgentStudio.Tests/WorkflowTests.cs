using AgentStudio.Application;
using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class WorkflowValidatorTests
{
    [Fact]
    public void Valid_linear_graph_passes()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "m", Text = "hi" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "m" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "m", TargetNodeId = "e" });

        Assert.Empty(WorkflowValidator.Validate(graph));
    }

    [Fact]
    public void Missing_start_fails()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new EndNode { Id = "e" });
        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("Start"));
    }

    [Fact]
    public void Missing_end_fails()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("End"));
    }

    [Fact]
    public void Cycle_is_allowed_since_phase_2()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "a", Text = "x" });
        graph.Nodes.Add(new MessageNode { Id = "b", Text = "y" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "a", TargetNodeId = "b" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "b", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "b", TargetNodeId = "e" });

        Assert.Empty(WorkflowValidator.Validate(graph));
    }

    [Fact]
    public void Condition_needs_both_branches()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ConditionNode { Id = "c", Left = "input", Right = "x" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "c" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "c", TargetNodeId = "e", Branch = "true" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("true") || e.Contains("false"));
    }

    [Fact]
    public void Unreachable_node_fails()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "orphan", Text = "x" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("not reachable"));
    }

    private static WorkflowGraph ValidParallelGraph()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ParallelNode { Id = "split" });
        graph.Nodes.Add(new MessageNode { Id = "a", Text = "a" });
        graph.Nodes.Add(new MessageNode { Id = "b", Text = "b" });
        graph.Nodes.Add(new JoinNode { Id = "join" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "split" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "split", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "split", TargetNodeId = "b" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "a", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "b", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "join", TargetNodeId = "e" });
        return graph;
    }

    [Fact]
    public void Valid_parallel_shape_passes()
    {
        Assert.Empty(WorkflowValidator.Validate(ValidParallelGraph()));
    }

    [Fact]
    public void Parallel_needs_at_least_two_branches()
    {
        var graph = ValidParallelGraph();
        graph.Edges.RemoveAll(e => e.Id is "3" or "5"); // drop the split->b and b->join edges
        graph.Nodes.RemoveAll(n => n.Id == "b");

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("at least 2 outgoing edges"));
    }

    [Fact]
    public void Parallel_branches_converging_on_different_joins_fails()
    {
        var graph = ValidParallelGraph();
        graph.Nodes.Add(new JoinNode { Id = "join2" });
        graph.Edges.RemoveAll(e => e.Id == "5");
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "b", TargetNodeId = "join2" });
        graph.Edges.Add(new WorkflowEdge { Id = "8", SourceNodeId = "join2", TargetNodeId = "e" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("different Join nodes"));
    }

    [Fact]
    public void Parallel_branch_reaching_end_instead_of_join_fails()
    {
        var graph = ValidParallelGraph();
        graph.Edges.RemoveAll(e => e.Id == "5");
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "b", TargetNodeId = "e" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("instead of a Join node"));
    }

    [Fact]
    public void Nested_parallel_region_fails()
    {
        var graph = ValidParallelGraph();
        graph.Nodes.Add(new ParallelNode { Id = "innerSplit" });
        graph.Edges.RemoveAll(e => e.Id == "2");
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "split", TargetNodeId = "innerSplit" });
        graph.Edges.Add(new WorkflowEdge { Id = "9", SourceNodeId = "innerSplit", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "10", SourceNodeId = "innerSplit", TargetNodeId = "b" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("nested parallel"));
    }

    [Fact]
    public void Join_reachable_bypassing_the_split_fails()
    {
        var graph = ValidParallelGraph();
        graph.Nodes.Add(new MessageNode { Id = "sneaky", Text = "x" });
        graph.Edges.Add(new WorkflowEdge { Id = "11", SourceNodeId = "s", TargetNodeId = "sneaky" });
        graph.Edges.Add(new WorkflowEdge { Id = "12", SourceNodeId = "sneaky", TargetNodeId = "join" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("bypassing the split"));
    }
}

public class ConditionEvaluatorTests
{
    private static string? Vars(Dictionary<string, string> d, string name) => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void Equals_comparison_works()
    {
        var vars = new Dictionary<string, string> { ["status"] = "approved" };
        var node = new ConditionNode { Id = "c", Left = "variables.status", Operator = ConditionOperator.Equals, Right = "approved" };
        Assert.True(ConditionEvaluator.Evaluate(node, n => Vars(vars, n)));
    }

    [Fact]
    public void Not_equals_comparison_works()
    {
        var vars = new Dictionary<string, string> { ["status"] = "rejected" };
        var node = new ConditionNode { Id = "c", Left = "variables.status", Operator = ConditionOperator.NotEquals, Right = "approved" };
        Assert.True(ConditionEvaluator.Evaluate(node, n => Vars(vars, n)));
    }

    [Fact]
    public void Contains_and_numeric_work()
    {
        var vars = new Dictionary<string, string> { ["text"] = "hello world", ["count"] = "42" };
        Assert.True(ConditionEvaluator.Evaluate(
            new ConditionNode { Id = "c", Left = "variables.text", Operator = ConditionOperator.Contains, Right = "world" },
            n => Vars(vars, n)));
        Assert.True(ConditionEvaluator.Evaluate(
            new ConditionNode { Id = "c", Left = "variables.count", Operator = ConditionOperator.GreaterThan, Right = "10" },
            n => Vars(vars, n)));
    }
}

public class TemplateTests
{
    [Fact]
    public void Expands_input_and_variables()
    {
        var vars = new Dictionary<string, string> { ["input"] = "hello", ["name"] = "world" };
        Assert.Equal("hello world!", WorkflowRunner.ExpandTemplate("{input} {variables.name}!", vars));
    }

    [Fact]
    public void Unknown_placeholder_is_preserved()
    {
        var vars = new Dictionary<string, string>();
        Assert.Equal("{variables.missing}", WorkflowRunner.ExpandTemplate("{variables.missing}", vars));
    }
}

public class WorkflowRunnerTests
{
    private sealed class FakeChatClient : Application.IChatClient
    {
        public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return "Hello ";
            await Task.Yield();
            yield return "there!";
        }
    }

    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public Application.IChatClient Create(ModelProviderConfig provider, string modelName) => new FakeChatClient();
    }

    private sealed class FakeHttp : ISecureHttpExecutor
    {
        public Task<string> ExecuteAsync(HttpNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default) =>
            Task.FromResult("{\"ok\":true}");
    }

    private sealed class FakeDocumentSearch : IDocumentSearchService
    {
        public Task<List<string>> SearchAsync(Guid agentId, string query, int topK, ModelProviderConfig provider, CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "chunk about " + query });
    }

    private sealed class FakeDatabaseQueryExecutor : IDatabaseQueryExecutor
    {
        public Task<string> ExecuteAsync(DatabaseQueryNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default) =>
            Task.FromResult("[]");
    }

    private sealed class FakeLogWriter : IExecutionLogWriter
    {
        public ExecutionLog Start(string conversationId, Guid agentId, int agentVersion) =>
            new() { ExecutionId = "test", ConversationId = conversationId, AgentId = agentId, AgentVersion = agentVersion };
        public ExecutionStep StartStep(ExecutionLog log, string nodeId, string nodeType) =>
            new() { NodeId = nodeId, NodeType = nodeType };
        public void CompleteStep(ExecutionStep step, string? detail = null) { }
        public void FailStep(ExecutionStep step, string error) { }
        public Task CompleteAsync(ExecutionLog log, string? error = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Empty by default — pre-populate via Add(...) only in tests that exercise SubAgentNode.</summary>
    private sealed class FakeAgentRepository : IAgentRepository
    {
        private readonly Dictionary<Guid, Agent> _agents = new();
        public void Add(Agent agent) => _agents[agent.Id] = agent;
        public Task<Agent?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_agents.GetValueOrDefault(id));
        public Task<List<Agent>> ListAsync(CancellationToken ct = default) => Task.FromResult(_agents.Values.ToList());
        public Task AddAsync(Agent agent, CancellationToken ct = default) { Add(agent); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProviderRepository : IProviderRepository
    {
        private readonly Dictionary<string, ModelProviderConfig> _providers = new();
        public void Add(ModelProviderConfig provider) => _providers[provider.Name] = provider;
        public Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(_providers.GetValueOrDefault(name));
        public Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default) => Task.FromResult(_providers.Values.ToList());
        public Task AddAsync(ModelProviderConfig provider, CancellationToken ct = default) { Add(provider); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (Agent agent, AgentVersion version, ModelProviderConfig provider, ConversationState conversation) Fixture(WorkflowGraph graph)
    {
        var agent = new Agent { Name = "t", ModelProviderName = "p", ModelName = "m" };
        var version = new AgentVersion { AgentId = agent.Id, Version = 1, Graph = graph };
        var provider = new ModelProviderConfig { Name = "p", BaseUrl = "http://localhost:11434" };
        var conversation = new ConversationState { ConversationId = "c1", AgentId = agent.Id, AgentVersion = 1 };
        return (agent, version, provider, conversation);
    }

    [Fact]
    public async Task Prompt_node_streams_llm_response()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new PromptNode { Id = "p", ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "p" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "p", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "hi"))
            output += chunk;

        Assert.Equal("Hello there!", output);
        Assert.Equal("Hello there!", conversation.Variables["r"]);
    }

    [Fact]
    public async Task Condition_routes_true_branch()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "status", Value = "approved" });
        graph.Nodes.Add(new ConditionNode { Id = "c", Left = "variables.status", Operator = ConditionOperator.Equals, Right = "approved" });
        graph.Nodes.Add(new MessageNode { Id = "mt", Text = "YES" });
        graph.Nodes.Add(new MessageNode { Id = "mf", Text = "NO" });
        graph.Nodes.Add(new EndNode { Id = "et" });
        graph.Nodes.Add(new EndNode { Id = "ef" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "c" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "c", TargetNodeId = "mt", Branch = "true" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "c", TargetNodeId = "mf", Branch = "false" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "mt", TargetNodeId = "et" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "mf", TargetNodeId = "ef" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "hi"))
            output += chunk;

        Assert.Equal("YES", output);
    }

    [Fact]
    public async Task Http_node_writes_result_variable()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new HttpNode { Id = "h", Method = "GET", Url = "https://api.example.com/x", ResultVariable = "api" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "Result: {variables.api}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "h" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "h", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "hi"))
            output += chunk;

        Assert.Equal("Result: {\"ok\":true}", output);
        Assert.Equal("{\"ok\":true}", conversation.Variables["api"]);
    }

    [Fact]
    public async Task Multi_turn_conversation_keeps_history()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "m", Text = "ack" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "m" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "m", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        await foreach (var _ in runner.RunAsync(agent, version, provider, conversation, "first")) { }
        await foreach (var _ in runner.RunAsync(agent, version, provider, conversation, "second")) { }

        Assert.Contains(conversation.Messages, m => m.Content == "first");
        Assert.Contains(conversation.Messages, m => m.Content == "second");
    }

    [Fact]
    public async Task Loop_runs_the_expected_number_of_iterations()
    {
        // start -> init(counter=0) -> check(counter<3) --true--> tick(message) -> incr(counter+1) -> back to check
        //                                            \--false--> end
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "init", Name = "counter", Value = "0" });
        graph.Nodes.Add(new ConditionNode { Id = "check", Left = "variables.counter", Operator = ConditionOperator.LessThan, Right = "3" });
        graph.Nodes.Add(new MessageNode { Id = "tick", Text = "." });
        graph.Nodes.Add(new VariableNode { Id = "incr", Name = "counter", Value = "{variables.counter+1}" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "init" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "init", TargetNodeId = "check" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "check", TargetNodeId = "tick", Branch = "true" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "check", TargetNodeId = "e", Branch = "false" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "tick", TargetNodeId = "incr" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "incr", TargetNodeId = "check" });

        Assert.Empty(WorkflowValidator.Validate(graph)); // cycles are allowed since phase 2

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("...", output); // three loop iterations
        Assert.Equal("3", conversation.Variables["counter"]);
    }

    [Fact]
    public async Task Infinite_loop_is_stopped_by_MaxSteps()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "loop", Text = "x" });
        graph.Nodes.Add(new EndNode { Id = "e" }); // unreachable, but End is required to pass validation
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "loop" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "loop", TargetNodeId = "loop" }); // self-loop, never exits

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);
        version.MaxSteps = 5;

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("maximum step count (5)", output);
    }

    [Fact]
    public async Task Parallel_branches_run_and_merge_at_join()
    {
        // start -> split --> http(sets "h", no visible text) ---\
        //                \-> prompt(LLM, streams + sets "p")  ---> join -> end (no template)
        // Http never emits visible text, so the only output is the Prompt branch's streamed
        // reply — proving both branches ran (via variables) without double-counting output.
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ParallelNode { Id = "split" });
        graph.Nodes.Add(new HttpNode { Id = "h", Method = "GET", Url = "https://api.example.com/x", ResultVariable = "h" });
        graph.Nodes.Add(new PromptNode { Id = "p", ResultVariable = "p" });
        graph.Nodes.Add(new JoinNode { Id = "join" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "split" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "split", TargetNodeId = "h" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "split", TargetNodeId = "p" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "h", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "p", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "join", TargetNodeId = "e" });

        Assert.Empty(WorkflowValidator.Validate(graph));

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("Hello there!", output);
        Assert.Equal("{\"ok\":true}", conversation.Variables["h"]);
        Assert.Equal("Hello there!", conversation.Variables["p"]);
    }

    [Fact]
    public async Task Parallel_output_is_flushed_in_declared_edge_order_not_interleaved()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ParallelNode { Id = "split" });
        graph.Nodes.Add(new MessageNode { Id = "a", Text = "A" });
        graph.Nodes.Add(new MessageNode { Id = "b", Text = "B" });
        graph.Nodes.Add(new JoinNode { Id = "join" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "split" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "split", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "split", TargetNodeId = "b" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "a", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "b", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "join", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("AB", output); // declared order (edge "2" before edge "3"), never "BA" or interleaved
    }

    [Fact]
    public async Task Parallel_merge_conflict_last_declared_branch_wins()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ParallelNode { Id = "split" });
        graph.Nodes.Add(new VariableNode { Id = "a", Name = "x", Value = "fromA" });
        graph.Nodes.Add(new VariableNode { Id = "b", Name = "x", Value = "fromB" });
        graph.Nodes.Add(new JoinNode { Id = "join" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.x}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "split" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "split", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "split", TargetNodeId = "b" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "a", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "b", TargetNodeId = "join" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "join", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("fromB", output); // edge "3" (split->b) declared after edge "2" (split->a)
    }

    [Fact]
    public async Task DocumentSearchNode_writes_joined_chunks_to_result_variable()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new DocumentSearchNode { Id = "search", Query = "{input}", TopK = 2, ResultVariable = "context" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.context}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "search" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "search", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "cats"))
            output += chunk;

        Assert.Equal("chunk about cats", output); // FakeDocumentSearch echoes the expanded query
        Assert.Equal("chunk about cats", conversation.Variables["context"]);
    }

    private static (Agent Agent, AgentVersion Version) PublishedAgent(string name, string providerName, WorkflowGraph graph)
    {
        var agent = new Agent { Name = name, ModelProviderName = providerName, ModelName = "m" };
        var version = new AgentVersion { AgentId = agent.Id, Version = 1, Status = AgentVersionStatus.Published, Graph = graph };
        agent.Versions.Add(version);
        return (agent, version);
    }

    [Fact]
    public async Task SubAgentNode_writes_target_agents_reply_to_result_variable()
    {
        var subGraph = new WorkflowGraph();
        subGraph.Nodes.Add(new StartNode { Id = "s" });
        subGraph.Nodes.Add(new MessageNode { Id = "m", Text = "hi from B" });
        subGraph.Nodes.Add(new EndNode { Id = "e" });
        subGraph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "m" });
        subGraph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "m", TargetNodeId = "e" });
        var (subAgent, _) = PublishedAgent("B", "p", subGraph);

        var agents = new FakeAgentRepository();
        agents.Add(subAgent);
        var providers = new FakeProviderRepository();
        providers.Add(new ModelProviderConfig { Name = "p", BaseUrl = "http://x" });

        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new SubAgentNode { Id = "sub", TargetAgentId = subAgent.Id, ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.r}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "sub" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "sub", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("hi from B", output);
        Assert.Equal("hi from B", conversation.Variables["r"]);
    }

    [Fact]
    public async Task SubAgentNode_cycle_is_stopped_by_depth_guard()
    {
        // Agent A's own graph calls A itself — an unbounded mutual (here: self-) recursion that
        // only the callDepth cap can stop, since WorkflowValidator can't see across agents.
        var agentId = Guid.NewGuid();
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new SubAgentNode { Id = "sub", TargetAgentId = agentId, ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.r}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "sub" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "sub", TargetNodeId = "e" });

        var agent = new Agent { Id = agentId, Name = "self-caller", ModelProviderName = "p", ModelName = "m" };
        var version = new AgentVersion { AgentId = agentId, Version = 1, Status = AgentVersionStatus.Published, Graph = graph };
        agent.Versions.Add(version);

        var agents = new FakeAgentRepository();
        agents.Add(agent);
        var providers = new FakeProviderRepository();
        providers.Add(new ModelProviderConfig { Name = "p", BaseUrl = "http://x" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor());
        var conversation = new ConversationState { ConversationId = "c1", AgentId = agentId, AgentVersion = 1 };

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider: providers.GetByNameAsync("p").Result!, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("call depth exceeded", output);
    }

    [Fact]
    public async Task SubAgentNode_target_without_published_version_fails_clearly()
    {
        var subAgent = new Agent { Name = "draft-only", ModelProviderName = "p", ModelName = "m" };
        subAgent.Versions.Add(new AgentVersion { AgentId = subAgent.Id, Version = 0, Status = AgentVersionStatus.Draft, Graph = new WorkflowGraph() });

        var agents = new FakeAgentRepository();
        agents.Add(subAgent);
        var providers = new FakeProviderRepository();
        providers.Add(new ModelProviderConfig { Name = "p", BaseUrl = "http://x" });

        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new SubAgentNode { Id = "sub", TargetAgentId = subAgent.Id, ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "sub" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "sub", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("no published version", output);
    }
}
