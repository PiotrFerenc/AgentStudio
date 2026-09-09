using System.Text;
using System.Text.Json;
using AgentStudio.Contracts;
using AgentStudio.Domain;

namespace AgentStudio.Application;

/// <summary>What GenerateGraphAsync returns: either a validated graph ready to insert, or the
/// reasons it couldn't produce one (bad/no JSON, unknown node type, structural validation
/// failure) — the caller shows Errors and never applies a partial/invalid graph.</summary>
public sealed record GraphGenerationResult(WorkflowGraphDto? Graph, List<string> Errors)
{
    public bool Success => Graph is not null;
}

/// <summary>Generates a workflow graph fragment from a plain-language description, using the
/// agent's own configured provider/model — no separate model picker. NodeCatalog (the same
/// catalog backing the palette tooltips and NodePropertiesEditor's field hints) is the entire
/// prompt: one source of truth for what node types/params exist, so a new node type that gets a
/// NodeCatalog entry is automatically generatable, no separate prompt to maintain.
///
/// The model is asked for WorkflowGraphDto-shaped JSON (not AgentStudioJson.Options' Domain
/// discriminated-union format — the flat Props dict is simpler for a model to produce
/// correctly). The result is round-tripped through GraphMapper.ToDomain + WorkflowValidator
/// before anything reaches the caller, so a hallucinated node type or a structurally broken
/// graph (missing Start, a Condition with no false edge, ...) comes back as a clear error
/// instead of landing on the canvas.</summary>
public sealed class GraphGenerationService
{
    private readonly IChatClientFactory _chatClients;
    private readonly IProviderRepository _providers;

    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    public GraphGenerationService(IChatClientFactory chatClients, IProviderRepository providers)
    {
        _chatClients = chatClients;
        _providers = providers;
    }

    public async Task<GraphGenerationResult> GenerateGraphAsync(Agent agent, string instructions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            return new GraphGenerationResult(null, ["Describe what the graph should do."]);

        var provider = await _providers.GetByNameAsync(agent.ModelProviderName, ct);
        if (provider is null)
            return new GraphGenerationResult(null, [$"Provider '{agent.ModelProviderName}' is not configured."]);

        var client = _chatClients.Create(provider, agent.ModelName);
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = BuildSystemPrompt() },
            new() { Role = "user", Content = instructions }
        };

        var buffer = new StringBuilder();
        await foreach (var chunk in client.StreamReplyAsync(messages, ct))
            buffer.Append(chunk);

        var json = StripCodeFence(buffer.ToString());

        WorkflowGraphDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WorkflowGraphDto>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            return new GraphGenerationResult(null, [$"Model did not return valid JSON: {ex.Message}"]);
        }

        if (dto is null || dto.Nodes.Count == 0)
            return new GraphGenerationResult(null, ["Model returned an empty graph."]);

        WorkflowGraph domain;
        try
        {
            domain = GraphMapper.ToDomain(dto);
        }
        catch (InvalidOperationException ex)
        {
            return new GraphGenerationResult(null, [ex.Message]);
        }

        var errors = WorkflowValidator.Validate(domain);
        return errors.Count > 0
            ? new GraphGenerationResult(null, errors)
            : new GraphGenerationResult(dto, []);
    }

    /// <summary>One entry per NodeCatalog node — type, what it does, and every editable
    /// parameter's key + meaning, plus the exact JSON shape expected back.</summary>
    public static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You design workflow graphs for an AI agent builder. Given a plain-language description, output ONLY a JSON object (no markdown, no commentary) with this exact shape:");
        sb.AppendLine("""{"nodes":[{"id":"n1","type":"...","label":"...","x":0,"y":0,"props":{...}}],"edges":[{"id":"e1","sourceNodeId":"n1","targetNodeId":"n2","branch":null}]}""");
        sb.AppendLine("Rules: exactly one node of type \"start\"; at least one node of type \"end\"; every node reachable from start; a \"condition\" node needs two outgoing edges with branch \"true\" and \"false\"; ids are your own short strings, unique within the graph; x/y are a rough left-to-right layout (200px steps are fine).");
        sb.AppendLine();
        sb.AppendLine("Available node types:");
        foreach (var node in NodeCatalog.Nodes.Values)
        {
            sb.AppendLine($"- \"{node.Type}\": {node.Summary}");
            foreach (var p in node.Params)
                sb.AppendLine($"    props.{p.Key}: {p.Description}");
        }
        return sb.ToString();
    }

    /// <summary>Strips a ```/```json fence some models wrap JSON output in — the model was
    /// asked not to, but this is cheap insurance against the common case where it does anyway.</summary>
    public static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        var withoutOpening = trimmed[(firstNewline + 1)..];
        var closingIndex = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return (closingIndex >= 0 ? withoutOpening[..closingIndex] : withoutOpening).Trim();
    }
}
