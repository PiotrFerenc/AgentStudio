using AgentStudio.Domain;
using AgentStudio.Contracts;

namespace AgentStudio.Application;

public sealed class AgentService
{
    private readonly IAgentRepository _agents;
    private readonly IApiKeyService _apiKeys;

    public AgentService(IAgentRepository agents, IApiKeyService apiKeys)
    {
        _agents = agents;
        _apiKeys = apiKeys;
    }

    public async Task<(Agent Agent, string RawApiKey)> CreateAsync(CreateAgentRequest request, CancellationToken ct = default)
    {
        var (rawKey, hash) = _apiKeys.Generate();
        var agent = new Agent
        {
            Name = request.Name,
            Description = request.Description,
            SystemInstructions = request.SystemInstructions,
            ModelProviderName = request.ModelProviderName,
            ModelName = request.ModelName,
            ApiKeyHash = hash
        };
        agent.Versions.Add(new AgentVersion
        {
            AgentId = agent.Id,
            Version = 0,
            Status = AgentVersionStatus.Draft,
            Graph = DefaultGraph()
        });
        await _agents.AddAsync(agent, ct);
        await _agents.SaveChangesAsync(ct);
        return (agent, rawKey);
    }

    public async Task<ValidationResultDto> UpdateDraftAsync(Guid agentId, WorkflowGraphDto graphDto, int? maxSteps = null, List<FormField>? formFields = null, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var graph = GraphMapper.ToDomain(graphDto);
        var errors = WorkflowValidator.Validate(graph);

        var draft = agent.Draft;
        if (draft is null)
        {
            draft = new AgentVersion { AgentId = agent.Id, Version = 0, Status = AgentVersionStatus.Draft, Graph = graph };
            agent.Versions.Add(draft);
        }
        else
        {
            draft.Graph = graph;
            draft.Status = errors.Count == 0 ? AgentVersionStatus.Validated : AgentVersionStatus.Draft;
        }
        if (maxSteps is > 0)
            draft.MaxSteps = maxSteps.Value;
        if (formFields is not null)
            draft.FormFields = formFields;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
        return new ValidationResultDto(errors.Count == 0, errors);
    }

    public async Task<AgentVersion> PublishAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        if (agent.Draft is null)
            throw new InvalidOperationException("Agent has no draft to publish.");

        var errors = WorkflowValidator.Validate(agent.Draft.Graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Draft is invalid: " + string.Join("; ", errors));

        var nextVersion = (agent.Versions.Count == 0 ? 0 : agent.Versions.Max(v => v.Version)) + 1;
        var published = new AgentVersion
        {
            AgentId = agent.Id,
            Version = nextVersion,
            Status = AgentVersionStatus.Published,
            Graph = agent.Draft.Graph,
            MaxSteps = agent.Draft.MaxSteps,
            FormFieldsJson = agent.Draft.FormFieldsJson,
            PublishedAt = DateTimeOffset.UtcNow
        };
        agent.Versions.Add(published);
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
        return published;
    }

    public bool VerifyApiKey(Agent agent, string? presentedKey) =>
        presentedKey is not null && _apiKeys.Verify(presentedKey, agent.ApiKeyHash);

    /// <summary>Generates a new API key, replacing the stored hash. Returns the raw key (shown once).</summary>
    public async Task<string> RegenerateApiKeyAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var (rawKey, hash) = _apiKeys.Generate();
        agent.ApiKeyHash = hash;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
        return rawKey;
    }

    /// <summary>Marks a published version as Unpublished. Not allowed for drafts.</summary>
    public async Task<AgentVersion> UnpublishAsync(Guid agentId, int version, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var v = agent.Versions.FirstOrDefault(x => x.Version == version)
            ?? throw new KeyNotFoundException("Version not found.");
        if (v.Status != AgentVersionStatus.Published)
            throw new InvalidOperationException("Only published versions can be unpublished.");
        v.Status = AgentVersionStatus.Unpublished;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
        return v;
    }

    /// <summary>Creates a NEW published version copying the graph of the given version (version number = max+1).</summary>
    public async Task<AgentVersion> RepublishAsync(Guid agentId, int version, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var source = agent.Versions.FirstOrDefault(x => x.Version == version)
            ?? throw new KeyNotFoundException("Version not found.");
        if (source.Status is AgentVersionStatus.Draft or AgentVersionStatus.Validated)
            throw new InvalidOperationException("Draft versions cannot be republished; publish the draft instead.");

        var nextVersion = (agent.Versions.Count == 0 ? 0 : agent.Versions.Max(v => v.Version)) + 1;
        var published = new AgentVersion
        {
            AgentId = agent.Id,
            Version = nextVersion,
            Status = AgentVersionStatus.Published,
            GraphJson = source.GraphJson,
            MaxSteps = source.MaxSteps,
            FormFieldsJson = source.FormFieldsJson,
            PublishedAt = DateTimeOffset.UtcNow
        };
        agent.Versions.Add(published);
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
        return published;
    }

    private static WorkflowGraph DefaultGraph()
    {
        var graph = new WorkflowGraph();
        graph.Nodes.Add(new StartNode { Id = "start", Label = "Start", X = 100, Y = 200 });
        graph.Nodes.Add(new PromptNode { Id = "prompt1", Label = "Assistant", X = 350, Y = 200, PromptTemplate = "", ResultVariable = "llmResult" });
        graph.Nodes.Add(new EndNode { Id = "end", Label = "End", X = 600, Y = 200 });
        graph.Edges.Add(new WorkflowEdge { Id = "e1", SourceNodeId = "start", TargetNodeId = "prompt1" });
        graph.Edges.Add(new WorkflowEdge { Id = "e2", SourceNodeId = "prompt1", TargetNodeId = "end" });
        return graph;
    }
}

public interface IApiKeyService
{
    (string RawKey, string Hash) Generate();
    bool Verify(string rawKey, string hash);
}

public sealed class ApiKeyService : IApiKeyService
{
    public (string RawKey, string Hash) Generate()
    {
        var raw = "ask_" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (raw, Hash(raw));
    }

    public bool Verify(string rawKey, string hash) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(Hash(rawKey)),
            System.Text.Encoding.UTF8.GetBytes(hash));

    private static string Hash(string raw) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}
