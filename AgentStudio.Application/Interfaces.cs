using AgentStudio.Domain;

namespace AgentStudio.Application;

public interface IAgentRepository
{
    Task<Agent?> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<Agent>> ListAsync(CancellationToken ct = default);
    Task AddAsync(Agent agent, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IProviderRepository
{
    Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default);
    Task AddAsync(ModelProviderConfig provider, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IExecutionLogRepository
{
    Task AddAsync(ExecutionLog log, CancellationToken ct = default);
    Task<ExecutionLog?> GetAsync(string executionId, CancellationToken ct = default);
    Task<List<ExecutionLog>> ListForAgentAsync(Guid agentId, int take = 50, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IConversationStore
{
    ConversationState GetOrCreate(string? conversationId, Guid agentId, int agentVersion);
    void Save(ConversationState state);
}

public interface IChatClientFactory
{
    IChatClient Create(ModelProviderConfig provider, string modelName);
}

public interface IChatClient
{
    IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

public interface ISecureHttpExecutor
{
    Task<string> ExecuteAsync(HttpNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default);
}

public interface IExecutionLogWriter
{
    ExecutionLog Start(string conversationId, Guid agentId, int agentVersion);
    ExecutionStep StartStep(ExecutionLog log, string nodeId, string nodeType);
    void CompleteStep(ExecutionStep step, string? detail = null);
    void FailStep(ExecutionStep step, string error);
    Task CompleteAsync(ExecutionLog log, string? error = null, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<List<User>> ListAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task DeleteAsync(User user, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IDocumentRepository
{
    Task<List<Document>> ListAsync(Guid agentId, CancellationToken ct = default);
    Task<Document?> GetAsync(Guid documentId, CancellationToken ct = default);
    Task<int> CountChunksAsync(Guid documentId, CancellationToken ct = default);
    Task<List<DocumentChunk>> ListChunksAsync(Guid agentId, CancellationToken ct = default);
    Task AddAsync(Document document, CancellationToken ct = default);
    Task AddChunksAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task DeleteAsync(Document document, CancellationToken ct = default);
    Task DeleteChunksAsync(Guid documentId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IEmbeddingClientFactory
{
    IEmbeddingClient Create(ModelProviderConfig provider, string modelName);
}

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>Chunks + embeds an uploaded document and stores it (phase 2, RAG).</summary>
public interface IDocumentIndexer
{
    Task<Document> IndexAsync(Guid agentId, string fileName, Stream content, ModelProviderConfig provider, CancellationToken ct = default);
    Task<List<Document>> ListAsync(Guid agentId, CancellationToken ct = default);
    Task DeleteAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>Embeds a query and returns the top-K most similar chunk texts for an agent's
/// uploaded documents (phase 2, RAG — used by DocumentSearchNode).</summary>
public interface IDocumentSearchService
{
    Task<List<string>> SearchAsync(Guid agentId, string query, int topK, ModelProviderConfig provider, CancellationToken ct = default);
}

public interface IDatabaseConnectionRepository
{
    Task<DatabaseConnectionConfig?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<DatabaseConnectionConfig?> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<DatabaseConnectionConfig>> ListAsync(CancellationToken ct = default);
    Task AddAsync(DatabaseConnectionConfig connection, CancellationToken ct = default);
    Task DeleteAsync(DatabaseConnectionConfig connection, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Runs a DatabaseQueryNode's parameterized SQL against its named connection
/// (phase 3) — read-only/SELECT-only guard, row cap, result serialized as JSON.</summary>
public interface IDatabaseQueryExecutor
{
    Task<string> ExecuteAsync(DatabaseQueryNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default);
}

/// <summary>Aggregates execution history (phase 3, analytics) — pure LINQ over the existing
/// ExecutionLog table, no separate tracking mechanism.</summary>
public interface IAnalyticsRepository
{
    Task<AnalyticsSummary> GetSummaryAsync(int days = 30, CancellationToken ct = default);
}

public sealed record AnalyticsSummary(
    int TotalExecutions,
    int FailedExecutions,
    int ActiveAgents,
    double AvgDurationSeconds,
    List<AgentUsage> ByAgent,
    List<DailyCount> ByDay);

public sealed record AgentUsage(Guid AgentId, string AgentName, int Executions, int Failed, double AvgDurationSeconds);

public sealed record DailyCount(DateOnly Date, int Executions);
