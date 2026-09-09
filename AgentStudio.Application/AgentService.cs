using AgentStudio.Domain;
using AgentStudio.Contracts;

namespace AgentStudio.Application;

public sealed class AgentService
{
    private readonly IAgentRepository _agents;
    private readonly IApiKeyService _apiKeys;
    private readonly IUserRepository _users;

    public AgentService(IAgentRepository agents, IApiKeyService apiKeys, IUserRepository users)
    {
        _agents = agents;
        _apiKeys = apiKeys;
        _users = users;
    }

    /// <summary>Owner is optional (null for the anonymous-style creation paths tests use directly)
    /// — every real caller through the Web project passes the current logged-in user, but nothing
    /// here requires one, since a null owner just means "only an Admin can edit it" (see
    /// <see cref="AgentAccess"/>), not an error.
    ///
    /// <paramref name="template"/> seeds the initial draft from an <see cref="AgentTemplate"/>
    /// (studio "New from template" flow) instead of the bare Start→Prompt→End
    /// <see cref="DefaultGraph"/> — the template's Graph and FormFields become the draft's, and
    /// its SystemInstructions fills in only when the request didn't specify one, so a caller
    /// that already wrote instructions keeps them.</summary>
    public async Task<(Agent Agent, string RawApiKey)> CreateAsync(CreateAgentRequest request, Guid? ownerId = null, string? ownerUsername = null, AgentTemplate? template = null, CancellationToken ct = default)
    {
        var (rawKey, hash) = _apiKeys.Generate();
        var agent = new Agent
        {
            Name = request.Name,
            Description = request.Description,
            SystemInstructions = string.IsNullOrWhiteSpace(request.SystemInstructions) ? template?.SystemInstructions ?? "" : request.SystemInstructions,
            ModelProviderName = request.ModelProviderName,
            ModelName = request.ModelName,
            ApiKeyHash = hash,
            OwnerId = ownerId,
            OwnerUsername = ownerUsername
        };
        agent.Versions.Add(new AgentVersion
        {
            AgentId = agent.Id,
            Version = 0,
            Status = AgentVersionStatus.Draft,
            Graph = template is null ? DefaultGraph() : GraphMapper.ToDomain(template.Graph),
            FormFields = template?.FormFields ?? new List<FormField>()
        });
        await _agents.AddAsync(agent, ct);
        await _agents.SaveChangesAsync(ct);
        return (agent, rawKey);
    }

    public async Task<ValidationResultDto> UpdateDraftAsync(
        Guid agentId,
        WorkflowGraphDto graphDto,
        int? maxSteps = null,
        List<FormField>? formFields = null,
        string? formResultMode = null,
        string? formResultTarget = null,
        bool? formResultMarkdown = null,
        CancellationToken ct = default)
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
        if (formResultMode is not null)
            draft.FormResultMode = formResultMode;
        if (formResultTarget is not null)
            draft.FormResultTarget = formResultTarget;
        if (formResultMarkdown is not null)
            draft.FormResultMarkdown = formResultMarkdown.Value;
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
            FormResultMode = agent.Draft.FormResultMode,
            FormResultTarget = agent.Draft.FormResultTarget,
            FormResultMarkdown = agent.Draft.FormResultMarkdown,
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

    /// <summary>Updates the agent's scheduled-trigger config (phase 11). A disabled/null
    /// interval never fires — enforced here (not just in the runner) so a stray "enabled" flag
    /// with no interval can't slip through and get treated as some implicit default.</summary>
    public async Task UpdateScheduleAsync(Guid agentId, bool enabled, int? intervalMinutes, string input, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        if (enabled && (intervalMinutes is null || intervalMinutes < 1))
            throw new InvalidOperationException("A schedule needs an interval of at least 1 minute.");
        agent.ScheduleEnabled = enabled;
        agent.ScheduleIntervalMinutes = intervalMinutes;
        agent.ScheduleInput = input;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
    }

    /// <summary>Changes the agent's default LLM provider/model — read-only everywhere before
    /// this (set once at creation, via CreateAsync/a template). Prompt/documentSearch nodes fall
    /// back to this when they don't override it themselves.</summary>
    public async Task UpdateProviderAsync(Guid agentId, string providerName, string modelName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(modelName))
            throw new InvalidOperationException("Provider and model name are required.");
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        agent.ModelProviderName = providerName;
        agent.ModelName = modelName;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
    }

    /// <summary>Replaces the agent's named config values (phase 12) — wholesale, not merged,
    /// same "the caller sends the full desired state" contract as UpdateDraftAsync's graph.</summary>
    public async Task UpdateEnvironmentVariablesAsync(Guid agentId, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        agent.EnvironmentVariables = variables;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _agents.SaveChangesAsync(ct);
    }

    /// <summary>Grants a user (looked up by username) edit access to an agent they don't own
    /// (phase 14). Idempotent — adding an existing collaborator again is a no-op, not a
    /// duplicate-key error.</summary>
    public async Task AddCollaboratorAsync(Guid agentId, string username, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        var user = await _users.GetByUsernameAsync(username, ct) ?? throw new KeyNotFoundException($"User '{username}' not found.");
        if (agent.Collaborators.Any(c => c.UserId == user.Id)) return;
        agent.Collaborators.Add(new AgentCollaborator { AgentId = agent.Id, UserId = user.Id, Username = user.Username });
        await _agents.SaveChangesAsync(ct);
    }

    public async Task RemoveCollaboratorAsync(Guid agentId, Guid userId, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(agentId, ct) ?? throw new KeyNotFoundException("Agent not found.");
        agent.Collaborators.RemoveAll(c => c.UserId == userId);
        await _agents.SaveChangesAsync(ct);
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
            FormResultMode = source.FormResultMode,
            FormResultTarget = source.FormResultTarget,
            FormResultMarkdown = source.FormResultMarkdown,
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
        // OutputTemplate is required here — no node emits live anymore, this is the only place
        // the run's visible output comes from.
        graph.Nodes.Add(new EndNode { Id = "end", Label = "End", X = 600, Y = 200, OutputTemplate = "{variables.llmResult}" });
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
