using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class ScheduledRunnerTests
{
    // Minimal local fakes — WorkflowRunner's own fakes in WorkflowTests.cs are private to that
    // class, so these are small, deliberately duplicated equivalents rather than reused.
    private sealed class FakeChatClient : IChatClient
    {
        public IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default) => Empty();
        private static async IAsyncEnumerable<string> Empty() { await Task.CompletedTask; yield break; }
    }
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        public IChatClient Create(ModelProviderConfig provider, string modelName) => new FakeChatClient();
    }
    private sealed class FakeHttp : ISecureHttpExecutor
    {
        public Task<string> ExecuteAsync(HttpNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default) => Task.FromResult("");
    }
    private sealed class FakeDocumentSearch : IDocumentSearchService
    {
        public Task<List<string>> SearchAsync(Guid agentId, string query, int topK, ModelProviderConfig provider, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
    private sealed class FakeDatabaseQueryExecutor : IDatabaseQueryExecutor
    {
        public Task<string> ExecuteAsync(DatabaseQueryNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default) => Task.FromResult("[]");
    }
    private sealed class FakeAgentCollectionStore : IAgentCollectionStore
    {
        public Task<string?> GetAsync(Guid agentId, string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(Guid agentId, string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<AgentCollectionEntry>> ListAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(new List<AgentCollectionEntry>());
        public Task DeleteAsync(Guid agentId, string key, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class FakePendingApprovalRepository : IPendingApprovalRepository
    {
        public Task<List<PendingApproval>> ListPendingAsync(CancellationToken ct = default) => Task.FromResult(new List<PendingApproval>());
        public Task<PendingApproval?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<PendingApproval?>(null);
        public Task AddAsync(PendingApproval approval, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class FakeLogWriter : IExecutionLogWriter
    {
        public ExecutionLog Start(string conversationId, Guid agentId, int agentVersion) => new() { ExecutionId = Guid.NewGuid().ToString(), ConversationId = conversationId, AgentId = agentId, AgentVersion = agentVersion };
        public ExecutionStep StartStep(ExecutionLog log, string nodeId, string nodeType) { var s = new ExecutionStep { NodeId = nodeId, NodeType = nodeType }; log.Steps.Add(s); return s; }
        public void CompleteStep(ExecutionStep step, string? detail = null) => step.Status = "completed";
        public void FailStep(ExecutionStep step, string error) => step.Status = "failed";
        public Task CompleteAsync(ExecutionLog log, string? error = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public readonly List<Agent> Agents = new();
        public Task<Agent?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Agents.FirstOrDefault(a => a.Id == id));
        public Task<List<Agent>> ListAsync(CancellationToken ct = default) => Task.FromResult(Agents.ToList());
        public Task AddAsync(Agent agent, CancellationToken ct = default) { Agents.Add(agent); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProviderRepository : IProviderRepository
    {
        public readonly List<ModelProviderConfig> Providers = new();
        public Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(Providers.FirstOrDefault(p => p.Name == name));
        public Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default) => Task.FromResult(Providers.ToList());
        public Task AddAsync(ModelProviderConfig provider, CancellationToken ct = default) { Providers.Add(provider); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static WorkflowGraph TrivialGraph()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "s" });
        graph.Nodes.Add(new EndNode { Id = "e" });
        graph.Edges.Add(new WorkflowEdge { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
        return graph;
    }

    private static Agent PublishedAgent(string name, bool scheduleEnabled, int? intervalMinutes, DateTimeOffset? lastRun)
    {
        var agent = new Agent { Name = name, ModelProviderName = "p", ModelName = "m", ScheduleEnabled = scheduleEnabled, ScheduleIntervalMinutes = intervalMinutes, LastScheduledRunAt = lastRun };
        agent.Versions.Add(new AgentVersion { AgentId = agent.Id, Version = 1, Status = AgentVersionStatus.Published, Graph = TrivialGraph() });
        return agent;
    }

    private static (ServiceProvider Provider, FakeAgentRepository Agents) BuildProvider(params Agent[] agents)
    {
        var fakeAgents = new FakeAgentRepository();
        fakeAgents.Agents.AddRange(agents);
        var fakeProviders = new FakeProviderRepository();
        fakeProviders.Providers.Add(new ModelProviderConfig { Name = "p", BaseUrl = "http://localhost:11434" });

        var runner = new WorkflowRunner(new FakeChatClientFactory(), new FakeHttp(), new FakeLogWriter(), new FakeDocumentSearch(), fakeAgents, fakeProviders, new FakeDatabaseQueryExecutor(), Array.Empty<IIntegrator>(), new FakeAgentCollectionStore(), new FakePendingApprovalRepository());

        var services = new ServiceCollection()
            .AddSingleton<IAgentRepository>(fakeAgents)
            .AddSingleton<IProviderRepository>(fakeProviders)
            .AddSingleton(runner)
            .BuildServiceProvider();
        return (services, fakeAgents);
    }

    private static ScheduledRunner NewRunner(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ScheduledRunnerOptions()), NullLogger<ScheduledRunner>.Instance);

    [Fact]
    public async Task Due_agent_runs_and_updates_LastScheduledRunAt()
    {
        var agent = PublishedAgent("due", scheduleEnabled: true, intervalMinutes: 5, lastRun: null);
        var (provider, _) = BuildProvider(agent);

        await NewRunner(provider).RunDueSchedulesAsync(CancellationToken.None);

        Assert.NotNull(agent.LastScheduledRunAt);
        Assert.True(DateTimeOffset.UtcNow - agent.LastScheduledRunAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Disabled_agent_is_never_run()
    {
        var agent = PublishedAgent("disabled", scheduleEnabled: false, intervalMinutes: 5, lastRun: null);
        var (provider, _) = BuildProvider(agent);

        await NewRunner(provider).RunDueSchedulesAsync(CancellationToken.None);

        Assert.Null(agent.LastScheduledRunAt);
    }

    [Fact]
    public async Task Agent_whose_interval_has_not_elapsed_is_skipped()
    {
        var recentRun = DateTimeOffset.UtcNow.AddMinutes(-1);
        var agent = PublishedAgent("not-due", scheduleEnabled: true, intervalMinutes: 30, lastRun: recentRun);
        var (provider, _) = BuildProvider(agent);

        await NewRunner(provider).RunDueSchedulesAsync(CancellationToken.None);

        Assert.Equal(recentRun, agent.LastScheduledRunAt);
    }

    [Fact]
    public async Task Agent_past_its_interval_runs_again()
    {
        var oldRun = DateTimeOffset.UtcNow.AddMinutes(-31);
        var agent = PublishedAgent("overdue", scheduleEnabled: true, intervalMinutes: 30, lastRun: oldRun);
        var (provider, _) = BuildProvider(agent);

        await NewRunner(provider).RunDueSchedulesAsync(CancellationToken.None);

        Assert.NotEqual(oldRun, agent.LastScheduledRunAt);
    }

    [Fact]
    public async Task One_agents_failure_does_not_block_other_due_agents()
    {
        var broken = PublishedAgent("broken", scheduleEnabled: true, intervalMinutes: 5, lastRun: null);
        broken.ModelProviderName = "does-not-exist"; // provider lookup fails
        var healthy = PublishedAgent("healthy", scheduleEnabled: true, intervalMinutes: 5, lastRun: null);
        var (provider, _) = BuildProvider(broken, healthy);

        await NewRunner(provider).RunDueSchedulesAsync(CancellationToken.None);

        Assert.Null(broken.LastScheduledRunAt); // fast-retry next poll, not marked as attempted
        Assert.NotNull(healthy.LastScheduledRunAt);
    }
}
