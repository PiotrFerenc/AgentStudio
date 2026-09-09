using System.Text;
using AgentStudio.Domain;

namespace AgentStudio.Application;

/// <summary>What a leaf node executor needs to do its work — the ambient run state any of them
/// might touch. Not every field is used by every executor (only PromptNode touches
/// Conversation/Emit) but a shared context keeps the INodeExecutor contract uniform instead of
/// each implementation declaring its own bespoke parameter list.</summary>
internal sealed record NodeExecutionContext(
    Dictionary<string, string> Variables,
    ConversationState Conversation,
    Agent Agent,
    ModelProviderConfig Provider,
    ChunkSink Emit,
    CancellationToken Ct);

/// <summary>One "leaf" node's execution — work that always continues to its single unconditional
/// outgoing edge (WorkflowRunner's NextByEdge with no branch), as opposed to a node that decides
/// its own next edge (Condition), fans out (Parallel), ends the run (End), or recurses back into
/// the engine (SubAgentNode) — those stay in WorkflowRunner.RunSegmentAsync's switch. Returns the
/// detail string RunStepAsync logs on success; throwing fails the step the same way for every
/// executor, so no per-node try/catch is left duplicated in the runner.</summary>
internal interface INodeExecutor
{
    Type NodeType { get; }
    Task<string?> ExecuteAsync(WorkflowNode node, NodeExecutionContext context);
}

internal abstract class NodeExecutor<TNode> : INodeExecutor where TNode : WorkflowNode
{
    public Type NodeType => typeof(TNode);
    Task<string?> INodeExecutor.ExecuteAsync(WorkflowNode node, NodeExecutionContext context) => ExecuteAsync((TNode)node, context);
    protected abstract Task<string?> ExecuteAsync(TNode node, NodeExecutionContext context);
}

/// <summary>PromptNode/DocumentSearchNode's per-node provider override (empty = inherit the run's
/// own provider). A set-but-unknown override name fails clearly rather than silently falling
/// back. Shared by the two executors that support an override — not folded into
/// NodeExecutionContext since it needs the node's own ProviderName, which only those two node
/// types have.</summary>
internal static class ProviderOverride
{
    public static async Task<ModelProviderConfig> ResolveAsync(string? overrideProviderName, ModelProviderConfig fallback, IProviderRepository providers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(overrideProviderName)) return fallback;
        return await providers.GetByNameAsync(overrideProviderName, ct)
            ?? throw new InvalidOperationException($"Provider '{overrideProviderName}' is not configured.");
    }
}

internal sealed class HttpNodeExecutor : NodeExecutor<HttpNode>
{
    private readonly ISecureHttpExecutor _http;
    public HttpNodeExecutor(ISecureHttpExecutor http) => _http = http;

    protected override async Task<string?> ExecuteAsync(HttpNode node, NodeExecutionContext context)
    {
        var result = await _http.ExecuteAsync(WorkflowRunner.Expand(node, context.Variables), context.Variables, context.Ct);
        context.Variables[node.ResultVariable] = result;
        return $"http {node.Method} {node.Url}: {result.Length} chars";
    }
}

internal sealed class DocumentSearchNodeExecutor : NodeExecutor<DocumentSearchNode>
{
    private readonly IDocumentSearchService _documentSearch;
    private readonly IProviderRepository _providers;
    public DocumentSearchNodeExecutor(IDocumentSearchService documentSearch, IProviderRepository providers)
    {
        _documentSearch = documentSearch;
        _providers = providers;
    }

    protected override async Task<string?> ExecuteAsync(DocumentSearchNode node, NodeExecutionContext context)
    {
        var query = WorkflowRunner.ExpandTemplate(node.Query, context.Variables);
        var provider = await ProviderOverride.ResolveAsync(node.ProviderName, context.Provider, _providers, context.Ct);
        var chunks = await _documentSearch.SearchAsync(context.Agent.Id, query, node.TopK, provider, context.Ct);
        context.Variables[node.ResultVariable] = string.Join("\n\n", chunks);
        return $"documentSearch: {chunks.Count} chunks";
    }
}

internal sealed class DatabaseQueryNodeExecutor : NodeExecutor<DatabaseQueryNode>
{
    private readonly IDatabaseQueryExecutor _databaseQuery;
    public DatabaseQueryNodeExecutor(IDatabaseQueryExecutor databaseQuery) => _databaseQuery = databaseQuery;

    protected override async Task<string?> ExecuteAsync(DatabaseQueryNode node, NodeExecutionContext context)
    {
        var result = await _databaseQuery.ExecuteAsync(node, context.Variables, context.Ct);
        context.Variables[node.ResultVariable] = result;
        return $"databaseQuery {node.ConnectionName}: {result.Length} chars";
    }
}

internal sealed class JsonParseNodeExecutor : NodeExecutor<JsonParseNode>
{
    protected override Task<string?> ExecuteAsync(JsonParseNode node, NodeExecutionContext context)
    {
        var inputText = WorkflowRunner.ExpandTemplate(node.Input, context.Variables);
        var result = JsonPathExtractor.Extract(inputText, node.Path);
        context.Variables[node.ResultVariable] = result;
        return Task.FromResult<string?>($"jsonParse {node.Path}: {result.Length} chars");
    }
}

internal sealed class ExpressionNodeExecutor : NodeExecutor<ExpressionNode>
{
    protected override Task<string?> ExecuteAsync(ExpressionNode node, NodeExecutionContext context)
    {
        var result = FormulaEvaluator.Evaluate(node.Formula, context.Variables);
        context.Variables[node.ResultVariable] = result;
        return Task.FromResult<string?>($"expression: {result}");
    }
}

internal sealed class CollectionGetNodeExecutor : NodeExecutor<CollectionGetNode>
{
    private readonly IAgentCollectionStore _collections;
    public CollectionGetNodeExecutor(IAgentCollectionStore collections) => _collections = collections;

    protected override async Task<string?> ExecuteAsync(CollectionGetNode node, NodeExecutionContext context)
    {
        var key = WorkflowRunner.ExpandTemplate(node.Key, context.Variables);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("collectionGet: Key is empty.");
        var stored = await _collections.GetAsync(context.Agent.Id, key, context.Ct);
        var value = stored ?? WorkflowRunner.ExpandTemplate(node.DefaultValue, context.Variables);
        context.Variables[node.ResultVariable] = value;
        return $"collectionGet {key}: {(stored is null ? "default" : "stored")} value";
    }
}

internal sealed class CollectionSetNodeExecutor : NodeExecutor<CollectionSetNode>
{
    private readonly IAgentCollectionStore _collections;
    public CollectionSetNodeExecutor(IAgentCollectionStore collections) => _collections = collections;

    protected override async Task<string?> ExecuteAsync(CollectionSetNode node, NodeExecutionContext context)
    {
        var key = WorkflowRunner.ExpandTemplate(node.Key, context.Variables);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("collectionSet: Key is empty.");
        var value = WorkflowRunner.ExpandTemplate(node.Value, context.Variables);
        await _collections.SetAsync(context.Agent.Id, key, value, context.Ct);
        return $"collectionSet {key}: {value.Length} chars";
    }
}

internal sealed class IntegratorNodeExecutor : NodeExecutor<IntegratorNode>
{
    private readonly IEnumerable<IIntegrator> _integrators;
    public IntegratorNodeExecutor(IEnumerable<IIntegrator> integrators) => _integrators = integrators;

    protected override async Task<string?> ExecuteAsync(IntegratorNode node, NodeExecutionContext context)
    {
        var impl = _integrators.FirstOrDefault(i => i.Name == node.IntegratorName)
            ?? throw new InvalidOperationException($"Integrator '{node.IntegratorName}' is not registered.");
        var expandedConfig = node.Config.ToDictionary(kv => kv.Key, kv => WorkflowRunner.ExpandTemplate(kv.Value, context.Variables));
        var result = await impl.ExecuteAsync(expandedConfig, context.Variables, context.Ct);
        context.Variables[node.ResultVariable] = result;
        return $"integrator {node.IntegratorName}: {result.Length} chars";
    }
}

internal sealed class PromptNodeExecutor : NodeExecutor<PromptNode>
{
    private readonly IChatClientFactory _chatClients;
    private readonly IProviderRepository _providers;
    public PromptNodeExecutor(IChatClientFactory chatClients, IProviderRepository providers)
    {
        _chatClients = chatClients;
        _providers = providers;
    }

    protected override async Task<string?> ExecuteAsync(PromptNode node, NodeExecutionContext context)
    {
        var promptText = WorkflowRunner.ExpandTemplate(node.PromptTemplate, context.Variables);
        List<ChatMessage> messages;
        lock (context.Conversation) messages = new List<ChatMessage>(context.Conversation.Messages);
        if (!string.IsNullOrWhiteSpace(promptText))
            messages.Add(new ChatMessage { Role = "user", Content = promptText });

        var provider = await ProviderOverride.ResolveAsync(node.ProviderName, context.Provider, _providers, context.Ct);
        var modelName = string.IsNullOrWhiteSpace(node.ModelName) ? context.Agent.ModelName : node.ModelName;
        var client = _chatClients.Create(provider, modelName);
        var buffer = new StringBuilder();
        await foreach (var chunk in client.StreamReplyAsync(messages, context.Ct))
        {
            buffer.Append(chunk);
            await context.Emit(chunk, context.Ct);
        }

        context.Variables[node.ResultVariable] = buffer.ToString();
        lock (context.Conversation) context.Conversation.Messages.Add(new ChatMessage { Role = "assistant", Content = buffer.ToString() });
        return $"llm: {buffer.Length} chars";
    }
}
