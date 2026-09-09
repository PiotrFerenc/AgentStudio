using System.Text.Json;
using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;

namespace AgentStudio.Web.Services;

/// <summary>Client-side facade over application services for Blazor pages.</summary>
public sealed class StudioApiClient
{
    private readonly IAgentRepository _agents;
    private readonly IProviderRepository _providers;
    private readonly IExecutionLogRepository _logs;
    private readonly AgentService _agentService;
    private readonly IConversationStore _conversations;
    private readonly WorkflowRunner _runner;
    private readonly UserService _users;
    private readonly IDocumentIndexer _documents;
    private readonly IDatabaseConnectionProvider _databaseConnections;
    private readonly IAnalyticsRepository _analytics;
    private readonly IEnumerable<IIntegrator> _integrators;
    private readonly IGraphComponentRepository _graphComponents;
    private readonly GraphGenerationService _graphGeneration;

    public StudioApiClient(
        IAgentRepository agents,
        IProviderRepository providers,
        IExecutionLogRepository logs,
        AgentService agentService,
        IConversationStore conversations,
        WorkflowRunner runner,
        UserService users,
        IDocumentIndexer documents,
        IDatabaseConnectionProvider databaseConnections,
        IAnalyticsRepository analytics,
        IEnumerable<IIntegrator> integrators,
        IGraphComponentRepository graphComponents,
        GraphGenerationService graphGeneration)
    {
        _agents = agents;
        _providers = providers;
        _logs = logs;
        _agentService = agentService;
        _conversations = conversations;
        _runner = runner;
        _users = users;
        _documents = documents;
        _databaseConnections = databaseConnections;
        _analytics = analytics;
        _integrators = integrators;
        _graphComponents = graphComponents;
        _graphGeneration = graphGeneration;
    }

    /// <summary>Name+description only — never the IIntegrator instance itself.</summary>
    public List<IntegratorSummary> ListIntegrators() =>
        _integrators.Select(i => new IntegratorSummary(i.Name, i.Description)).ToList();

    public Task<AnalyticsSummary> GetAnalyticsAsync(int days = 30, CancellationToken ct = default) =>
        _analytics.GetSummaryAsync(days, ct);

    public Task<List<User>> ListUsersAsync(CancellationToken ct = default) => _users.ListAsync(ct);

    public Task<List<Agent>> ListAgentsAsync(CancellationToken ct = default) => _agents.ListAsync(ct);
    public Task<Agent?> GetAgentAsync(Guid id, CancellationToken ct = default) => _agents.GetAsync(id, ct);

    public async Task<(Agent Agent, string RawKey)> CreateAgentAsync(string name, string description, string instructions, string providerName, string modelName, Guid? ownerId = null, string? ownerUsername = null, CancellationToken ct = default) =>
        await _agentService.CreateAsync(new CreateAgentRequest(name, description, instructions, providerName, modelName), ownerId, ownerUsername, ct);

    public Task<ValidationResultDto> SaveDraftAsync(
        Guid agentId, WorkflowGraphDto graph, int? maxSteps = null, List<FormField>? formFields = null,
        string? formResultMode = null, string? formResultTarget = null, bool? formResultMarkdown = null,
        CancellationToken ct = default) =>
        _agentService.UpdateDraftAsync(agentId, graph, maxSteps, formFields, formResultMode, formResultTarget, formResultMarkdown, ct);

    public Task<AgentVersion> PublishAsync(Guid agentId, CancellationToken ct = default) =>
        _agentService.PublishAsync(agentId, ct);

    public Task<string> RegenerateApiKeyAsync(Guid agentId, CancellationToken ct = default) =>
        _agentService.RegenerateApiKeyAsync(agentId, ct);

    public Task UpdateScheduleAsync(Guid agentId, bool enabled, int? intervalMinutes, string input, CancellationToken ct = default) =>
        _agentService.UpdateScheduleAsync(agentId, enabled, intervalMinutes, input, ct);

    public Task UpdateEnvironmentVariablesAsync(Guid agentId, Dictionary<string, string> variables, CancellationToken ct = default) =>
        _agentService.UpdateEnvironmentVariablesAsync(agentId, variables, ct);

    public Task AddCollaboratorAsync(Guid agentId, string username, CancellationToken ct = default) =>
        _agentService.AddCollaboratorAsync(agentId, username, ct);

    public Task RemoveCollaboratorAsync(Guid agentId, Guid userId, CancellationToken ct = default) =>
        _agentService.RemoveCollaboratorAsync(agentId, userId, ct);

    public Task<AgentVersion> UnpublishAsync(Guid agentId, int version, CancellationToken ct = default) =>
        _agentService.UnpublishAsync(agentId, version, ct);

    public Task<AgentVersion> RepublishAsync(Guid agentId, int version, CancellationToken ct = default) =>
        _agentService.RepublishAsync(agentId, version, ct);

    public Task<List<ModelProviderConfig>> ListProvidersAsync(CancellationToken ct = default) => _providers.ListAsync(ct);

    public Task<List<ExecutionLog>> ListLogsAsync(Guid agentId, CancellationToken ct = default) =>
        _logs.ListForAgentAsync(agentId, 50, ct);

    public async Task<(ConversationState Conversation, ModelProviderConfig Provider, AgentVersion Version)?> StartTestChatAsync(Agent agent, string? conversationId, CancellationToken ct = default)
    {
        var provider = await _providers.GetByNameAsync(agent.ModelProviderName, ct);
        var version = agent.Draft ?? agent.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
        if (provider is null || version is null) return null;
        var conversation = _conversations.GetOrCreate(conversationId, agent.Id, version.Version);
        return (conversation, provider, version);
    }

    public IAsyncEnumerable<string> RunTestChatAsync(Agent agent, AgentVersion version, ModelProviderConfig provider, ConversationState conversation, string message, CancellationToken ct = default)
    {
        var stream = _runner.RunAsync(agent, version, provider, conversation, message, ct);
        return Wrap(stream, conversation);
    }

    private async IAsyncEnumerable<string> Wrap(IAsyncEnumerable<string> inner, ConversationState conversation)
    {
        await foreach (var chunk in inner)
            yield return chunk;
        _conversations.Save(conversation);
    }

    public Task<List<Document>> ListDocumentsAsync(Guid agentId, CancellationToken ct = default) => _documents.ListAsync(agentId, ct);

    public async Task<Document> UploadDocumentAsync(Guid agentId, string fileName, Stream content, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var provider = await _providers.GetByNameAsync(agent.ModelProviderName, ct)
            ?? throw new InvalidOperationException($"Provider '{agent.ModelProviderName}' is not configured.");
        return await _documents.IndexAsync(agentId, fileName, content, provider, ct);
    }

    public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default) => _documents.DeleteAsync(documentId, ct);

    /// <summary>Read-only — connections are defined in appsettings.json ("DatabaseConnections"),
    /// not editable at runtime.</summary>
    public List<DatabaseConnectionConfig> ListDatabaseConnections() => _databaseConnections.List();

    /// <summary>Every saved graph component with its graph already deserialized — the graph
    /// editor's "Insert component" needs the actual content up front (cloning happens
    /// synchronously in the editor), not just names, so this fetches both together rather than
    /// forcing a round trip per insert.</summary>
    public async Task<List<(GraphComponentSummary Summary, WorkflowGraphDto Graph)>> ListGraphComponentsWithGraphsAsync(CancellationToken ct = default)
    {
        var components = await _graphComponents.ListAsync(ct);
        return components
            .Select(c => (
                new GraphComponentSummary(c.Id, c.Name, c.Description, c.CreatedAt),
                JsonSerializer.Deserialize<WorkflowGraphDto>(c.GraphJson, AgentStudioJson.Options) ?? new WorkflowGraphDto()))
            .ToList();
    }

    public async Task SaveGraphComponentAsync(string name, WorkflowGraphDto graph, CancellationToken ct = default)
    {
        var component = new GraphComponent
        {
            Name = name,
            GraphJson = JsonSerializer.Serialize(graph, AgentStudioJson.Options)
        };
        await _graphComponents.AddAsync(component, ct);
        await _graphComponents.SaveChangesAsync(ct);
    }

    public async Task DeleteGraphComponentAsync(Guid id, CancellationToken ct = default)
    {
        var component = await _graphComponents.GetAsync(id, ct);
        if (component is null) return;
        await _graphComponents.DeleteAsync(component, ct);
        await _graphComponents.SaveChangesAsync(ct);
    }

    /// <summary>Runs one node from the (possibly unsaved) draft graph against sample variables —
    /// the graph editor's "Test node" panel. Uses whatever graph the caller currently has open,
    /// not the persisted draft, so it reflects unsaved edits too.</summary>
    public async Task<NodeDebugResult> DebugNodeAsync(Guid agentId, WorkflowGraphDto graphDto, string nodeId, Dictionary<string, string> sampleVariables, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var provider = await _providers.GetByNameAsync(agent.ModelProviderName, ct)
            ?? throw new InvalidOperationException($"Provider '{agent.ModelProviderName}' is not configured.");
        var graph = GraphMapper.ToDomain(graphDto);
        return await _runner.DebugNodeAsync(agent, provider, graph, nodeId, sampleVariables, ct);
    }

    /// <summary>Generates a graph fragment from a plain-language description — the graph
    /// editor's "Generate with AI" panel. Uses the agent's own configured provider/model.</summary>
    public async Task<GraphGenerationResult> GenerateGraphAsync(Guid agentId, string instructions, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        return await _graphGeneration.GenerateGraphAsync(agent, instructions, ct);
    }
}
