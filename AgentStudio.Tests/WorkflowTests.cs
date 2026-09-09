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

    [Fact]
    public void Approval_node_needs_both_approved_and_rejected_edges()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ApprovalNode { Id = "a", Message = "ok?" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "a", TargetNodeId = "e", Branch = "approved" });

        Assert.Contains(WorkflowValidator.Validate(graph), e => e.Contains("approved") && e.Contains("rejected"));
    }

    [Fact]
    public void Approval_node_with_both_branches_passes()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ApprovalNode { Id = "a", Message = "ok?" });
        graph.Nodes.Add(new MessageNode { Id = "yes", Text = "approved" });
        graph.Nodes.Add(new MessageNode { Id = "no", Text = "rejected" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "a", TargetNodeId = "yes", Branch = "approved" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "a", TargetNodeId = "no", Branch = "rejected" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "yes", TargetNodeId = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "no", TargetNodeId = "e" });

        Assert.Empty(WorkflowValidator.Validate(graph));
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

    private sealed class FakeAgentCollectionStore : IAgentCollectionStore
    {
        private readonly Dictionary<(Guid, string), string> _store = new();

        public Task<string?> GetAsync(Guid agentId, string key, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue((agentId, key), out var v) ? v : null);

        public Task SetAsync(Guid agentId, string key, string value, CancellationToken ct = default)
        {
            _store[(agentId, key)] = value;
            return Task.CompletedTask;
        }

        public Task<List<AgentCollectionEntry>> ListAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult(_store.Where(kv => kv.Key.Item1 == agentId)
                .Select(kv => new AgentCollectionEntry { AgentId = agentId, Key = kv.Key.Item2, Value = kv.Value })
                .ToList());

        public Task DeleteAsync(Guid agentId, string key, CancellationToken ct = default)
        {
            _store.Remove((agentId, key));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePendingApprovalRepository : IPendingApprovalRepository
    {
        public readonly List<PendingApproval> Items = new();
        public Task<List<PendingApproval>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.Where(a => a.Status == ApprovalStatus.Pending).ToList());
        public Task<PendingApproval?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.FirstOrDefault(a => a.Id == id));
        public Task AddAsync(PendingApproval approval, CancellationToken ct = default) { Items.Add(approval); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "hi"))
            output += chunk;

        Assert.Equal("Hello there!", output);
        Assert.Equal("Hello there!", conversation.Variables["r"]);
    }

    [Fact]
    public async Task JsonParseNode_extracts_value_and_writes_it_to_variable()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "apiResponse", Value = """{"user":{"name":"Anna","tags":["a","b"]}}""" });
        graph.Nodes.Add(new JsonParseNode { Id = "j", Input = "{variables.apiResponse}", Path = "user.name", ResultVariable = "userName" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.userName}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "j" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "j", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("Anna", output);
        Assert.Equal("Anna", conversation.Variables["userName"]);
    }

    [Fact]
    public async Task JsonParseNode_missing_path_fails_clearly_instead_of_hanging()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "apiResponse", Value = """{"user":{"name":"Anna"}}""" });
        graph.Nodes.Add(new JsonParseNode { Id = "j", Input = "{variables.apiResponse}", Path = "user.email", ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "j" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "j", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("'email'", output);
    }

    [Fact]
    public async Task FormValues_are_merged_into_variables_before_run()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "hello {variables.city}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "", formValues: new Dictionary<string, string> { ["city"] = "Warsaw" }))
            output += chunk;

        Assert.Equal("hello Warsaw", output);
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
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

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("no published version", output);
    }

    private sealed class FakeIntegrator : IIntegrator
    {
        public string Name => "fake.echo";
        public string Description => "Echoes its config back as JSON, for tests.";
        public IReadOnlyDictionary<string, string>? LastConfig { get; private set; }

        public Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
        {
            LastConfig = config;
            return Task.FromResult(string.Join(",", config.Select(kv => $"{kv.Key}={kv.Value}")));
        }
    }

    [Fact]
    public async Task IntegratorNode_expands_config_and_writes_result_to_variable()
    {
        var integrator = new FakeIntegrator();
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new IntegratorNode
        {
            Id = "i",
            IntegratorName = "fake.echo",
            Config = new Dictionary<string, string> { ["title"] = "issue for {input}" },
            ResultVariable = "r"
        });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.r}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "i" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "i", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), new IIntegrator[] { integrator }, new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "cats"))
            output += chunk;

        Assert.Equal("title=issue for cats", output);
        Assert.Equal("title=issue for cats", conversation.Variables["r"]);
        Assert.Equal("issue for cats", integrator.LastConfig!["title"]); // template expanded before reaching the integrator
    }

    [Fact]
    public async Task IntegratorNode_unknown_name_fails_clearly()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new IntegratorNode { Id = "i", IntegratorName = "does-not-exist", ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "i" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "i", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("not registered", output);
    }

    [Fact]
    public async Task DebugNodeAsync_runs_one_node_against_sample_variables()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new JsonParseNode { Id = "j", Input = "{variables.apiResponse}", Path = "user.name", ResultVariable = "userName" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "j" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "j", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, _, provider, _) = Fixture(graph);
        var sample = new Dictionary<string, string> { ["apiResponse"] = """{"user":{"name":"Anna"}}""" };

        var result = await runner.DebugNodeAsync(agent, provider, graph, "j", sample);

        Assert.True(result.Success);
        Assert.Equal("Anna", result.Variables["userName"]);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task DebugNodeAsync_stops_after_the_target_node_even_when_it_has_downstream_edges()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "x", Value = "1" });
        graph.Nodes.Add(new MessageNode { Id = "m", Text = "should not run" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "m" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "m", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, _, provider, _) = Fixture(graph);

        var result = await runner.DebugNodeAsync(agent, provider, graph, "v", new Dictionary<string, string>());

        Assert.True(result.Success);
        Assert.Equal("1", result.Variables["x"]);
        Assert.Equal("", result.EmittedText); // the downstream MessageNode never ran
    }

    [Fact]
    public async Task DebugNodeAsync_reports_node_failure_without_throwing()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new JsonParseNode { Id = "j", Input = "not json", Path = "a", ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "j" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "j", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, _, provider, _) = Fixture(graph);

        var result = await runner.DebugNodeAsync(agent, provider, graph, "j", new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("not valid JSON", result.Error);
    }

    [Theory]
    [InlineData("s", "start")]
    [InlineData("start-parallel", "parallel")]
    public async Task DebugNodeAsync_rejects_structural_node_types(string nodeId, string expectedTypeInMessage)
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ParallelNode { Id = "start-parallel" });
        graph.Nodes.Add(new JoinNode { Id = "join1" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "start-parallel" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "start-parallel", TargetNodeId = "join1" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "join1", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, _, provider, _) = Fixture(graph);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DebugNodeAsync(agent, provider, graph, nodeId, new Dictionary<string, string>()));
        Assert.Contains(expectedTypeInMessage, ex.Message);
    }

    [Fact]
    public async Task ExpressionNode_computes_a_formula_and_writes_it_to_variable()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "age", Value = "20" });
        graph.Nodes.Add(new ExpressionNode { Id = "x", Formula = "IF({variables.age} >= 18, \"adult\", \"minor\")", ResultVariable = "category" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.category}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "x" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "x", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("adult", output);
        Assert.Equal("adult", conversation.Variables["category"]);
    }

    [Fact]
    public async Task ExpressionNode_bad_formula_fails_clearly_instead_of_hanging()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new ExpressionNode { Id = "x", Formula = "NOPE(1)", ResultVariable = "r" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "x" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "x", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("unknown function", output);
    }

    [Fact]
    public async Task CollectionSet_then_get_survives_across_separate_conversations()
    {
        // The whole point of a collection (vs. a normal variable) is that it outlives a single
        // run/conversation — this proves it via two completely separate RunAsync calls against
        // the same shared collection store.
        var setGraph = new WorkflowGraph();
        setGraph.Nodes.Add(new StartNode { Id = "s" });
        setGraph.Nodes.Add(new CollectionSetNode { Id = "cs", Key = "greeting", Value = "{input}" });
        setGraph.Nodes.Add(new EndNode { Id = "e" });
        setGraph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "cs" });
        setGraph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "cs", TargetNodeId = "e" });

        var getGraph = new WorkflowGraph();
        getGraph.Nodes.Add(new StartNode { Id = "s" });
        getGraph.Nodes.Add(new CollectionGetNode { Id = "cg", Key = "greeting", ResultVariable = "g" });
        getGraph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.g}" });
        getGraph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "cg" });
        getGraph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "cg", TargetNodeId = "e" });

        var sharedStore = new FakeAgentCollectionStore();
        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), sharedStore, new FakePendingApprovalRepository());

        var (agent, setVersion, provider, firstConversation) = Fixture(setGraph);
        await foreach (var _ in runner.RunAsync(agent, setVersion, provider, firstConversation, "hello there")) { }

        // A brand new conversation for the same agent — nothing shared except the agent id.
        var secondConversation = new ConversationState { ConversationId = "c2", AgentId = agent.Id, AgentVersion = 1 };
        var getVersion = new AgentVersion { AgentId = agent.Id, Version = 1, Graph = getGraph };
        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, getVersion, provider, secondConversation, "go"))
            output += chunk;

        Assert.Equal("hello there", output);
    }

    [Fact]
    public async Task CollectionGet_uses_default_value_when_key_was_never_set()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new CollectionGetNode { Id = "cg", Key = "missing", DefaultValue = "fallback", ResultVariable = "g" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.g}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "cg" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "cg", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("fallback", output);
    }

    [Fact]
    public async Task CollectionSet_two_different_agents_never_see_each_others_keys()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new CollectionSetNode { Id = "cs", Key = "k", Value = "{input}" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "cs" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "cs", TargetNodeId = "e" });

        var sharedStore = new FakeAgentCollectionStore();
        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), sharedStore, new FakePendingApprovalRepository());

        var (agentA, versionA, provider, convA) = Fixture(graph);
        await foreach (var _ in runner.RunAsync(agentA, versionA, provider, convA, "from agent A")) { }

        Assert.Null(await sharedStore.GetAsync(Guid.NewGuid(), "k")); // sanity: unrelated agent id has nothing
        Assert.Equal("from agent A", await sharedStore.GetAsync(agentA.Id, "k"));
    }

    [Fact]
    public async Task CollectionSet_empty_key_fails_clearly_instead_of_hanging()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new CollectionSetNode { Id = "cs", Key = "", Value = "x" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "cs" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "cs", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Contains("[error]", output);
        Assert.Contains("Key is empty", output);
    }

    [Fact]
    public async Task Agent_environment_variables_are_reachable_as_variables_env_name()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.env.API_URL}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);
        agent.EnvironmentVariables["API_URL"] = "https://example.com";

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go"))
            output += chunk;

        Assert.Equal("https://example.com", output);
    }

    [Fact]
    public async Task FormValues_can_override_an_environment_variable_of_the_same_name()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new EndNode { Id = "e", OutputTemplate = "{variables.env.API_URL}" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, version, provider, conversation) = Fixture(graph);
        agent.EnvironmentVariables["API_URL"] = "https://default.example.com";

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "go", formValues: new Dictionary<string, string> { ["env.API_URL"] = "https://override.example.com" }))
            output += chunk;

        Assert.Equal("https://override.example.com", output);
    }

    [Fact]
    public async Task DebugNodeAsync_also_sees_agent_environment_variables()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new MessageNode { Id = "m", Text = "{variables.env.GREETING}" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "m" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "m", TargetNodeId = "e" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());
        var (agent, _, provider, _) = Fixture(graph);
        agent.EnvironmentVariables["GREETING"] = "hi";

        var result = await runner.DebugNodeAsync(agent, provider, graph, "m", new Dictionary<string, string>());

        Assert.Equal("hi", result.EmittedText);
    }

    private static WorkflowGraph ApprovalGraph()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new VariableNode { Id = "v", Name = "amount", Value = "{input}" });
        graph.Nodes.Add(new ApprovalNode { Id = "a", Message = "Approve {variables.amount}?" });
        graph.Nodes.Add(new MessageNode { Id = "yes", Text = "refunded {variables.amount}" });
        graph.Nodes.Add(new MessageNode { Id = "no", Text = "denied" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "v" });
        graph.Edges.Add(new WorkflowEdge { Id = "2", SourceNodeId = "v", TargetNodeId = "a" });
        graph.Edges.Add(new WorkflowEdge { Id = "3", SourceNodeId = "a", TargetNodeId = "yes", Branch = "approved" });
        graph.Edges.Add(new WorkflowEdge { Id = "4", SourceNodeId = "a", TargetNodeId = "no", Branch = "rejected" });
        graph.Edges.Add(new WorkflowEdge { Id = "5", SourceNodeId = "yes", TargetNodeId = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "6", SourceNodeId = "no", TargetNodeId = "e" });
        return graph;
    }

    [Fact]
    public async Task Reaching_approval_node_suspends_the_run_and_creates_a_pending_approval()
    {
        var graph = ApprovalGraph();
        var approvals = new FakePendingApprovalRepository();
        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), new FakeAgentRepository(), new FakeProviderRepository(), new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), approvals);
        var (agent, version, provider, conversation) = Fixture(graph);

        var output = "";
        await foreach (var chunk in runner.RunAsync(agent, version, provider, conversation, "100"))
            output += chunk;

        Assert.Contains("[awaiting approval] Approve 100?", output);
        Assert.DoesNotContain("refunded", output); // the run stopped, neither branch ran yet
        Assert.DoesNotContain("denied", output);
        var pending = Assert.Single(approvals.Items);
        Assert.Equal("a", pending.NodeId);
        Assert.Equal(ApprovalStatus.Pending, pending.Status);
        Assert.Equal("100", pending.Variables["amount"]);
    }

    private static (WorkflowRunner Runner, FakeAgentRepository Agents, FakeProviderRepository Providers, FakePendingApprovalRepository Approvals) ApprovalRunner()
    {
        var agents = new FakeAgentRepository();
        var providers = new FakeProviderRepository();
        providers.Add(new ModelProviderConfig { Name = "p", BaseUrl = "http://x" });
        var approvals = new FakePendingApprovalRepository();
        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), agents, providers, new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), approvals);
        return (runner, agents, providers, approvals);
    }

    private static async Task<PendingApproval> RunUpToApproval((WorkflowRunner Runner, FakeAgentRepository Agents, FakeProviderRepository Providers, FakePendingApprovalRepository Approvals) rig, string input)
    {
        var graph = ApprovalGraph();
        var agent = new Agent { Name = "t", ModelProviderName = "p", ModelName = "m" };
        var version = new AgentVersion { AgentId = agent.Id, Version = 1, Status = AgentVersionStatus.Published, Graph = graph };
        agent.Versions.Add(version);
        rig.Agents.Add(agent);
        var provider = (await rig.Providers.GetByNameAsync("p"))!;
        var conversation = new ConversationState { ConversationId = "c1", AgentId = agent.Id, AgentVersion = 1 };

        await foreach (var _ in rig.Runner.RunAsync(agent, version, provider, conversation, input)) { }
        return Assert.Single(rig.Approvals.Items);
    }

    [Fact]
    public async Task ResumeApprovalAsync_approved_continues_down_the_approved_branch_with_snapshotted_variables()
    {
        var rig = ApprovalRunner();
        var pending = await RunUpToApproval(rig, "100");

        var result = await rig.Runner.ResumeApprovalAsync(pending.Id, approved: true, "alice");

        Assert.Equal("refunded 100", result);
        Assert.Equal(ApprovalStatus.Approved, pending.Status);
        Assert.Equal("alice", pending.DecidedBy);
        Assert.NotNull(pending.DecidedAt);
    }

    [Fact]
    public async Task ResumeApprovalAsync_rejected_continues_down_the_rejected_branch()
    {
        var rig = ApprovalRunner();
        var pending = await RunUpToApproval(rig, "50");

        var result = await rig.Runner.ResumeApprovalAsync(pending.Id, approved: false, "bob");

        Assert.Equal("denied", result);
        Assert.Equal(ApprovalStatus.Rejected, pending.Status);
    }

    [Fact]
    public async Task ResumeApprovalAsync_missing_id_fails_clearly()
    {
        var rig = ApprovalRunner();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Runner.ResumeApprovalAsync(Guid.NewGuid(), true, "x"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ResumeApprovalAsync_already_decided_fails_clearly()
    {
        var rig = ApprovalRunner();
        var already = new PendingApproval { AgentId = Guid.NewGuid(), AgentVersion = 1, NodeId = "a", Status = ApprovalStatus.Approved };
        rig.Approvals.Items.Add(already);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Runner.ResumeApprovalAsync(already.Id, true, "x"));
        Assert.Contains("already", ex.Message);
    }
}
