namespace AgentStudio.Domain;

public sealed class ModelProviderConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string BaseUrl { get; set; }
    public string DefaultModel { get; set; } = "";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Optional embedding model name (phase 2, RAG). Required only for agents using a
    /// DocumentSearchNode; leave blank for providers/agents that don't need embeddings.</summary>
    public string? EmbeddingModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An uploaded, indexed source document for RAG (phase 2). Raw bytes live on the local
/// filesystem at StoragePath; DocumentChunk rows hold the searchable text + embeddings.</summary>
public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public required string FileName { get; set; }
    public required string StoragePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid AgentId { get; set; }
    public int ChunkIndex { get; set; }
    public required string Text { get; set; }
    public List<float> Embedding { get; set; } = new();
}

/// <summary>A named database connection agents can query via DatabaseQueryNode (phase 3).
/// Defined in appsettings.json under "DatabaseConnections" (an array), not admin-editable at
/// runtime — connection strings are operational/deployment config, like ConnectionStrings:
/// AgentStudio itself, not user data that belongs in the app's own database.</summary>
public sealed class DatabaseConnectionConfig
{
    public required string Name { get; set; }
    public required string ConnectionString { get; set; }
    /// <summary>Only "postgres" is implemented today (Npgsql is already a project dependency) —
    /// this field exists so a future connector type doesn't need a breaking config-shape change.
    /// Any other value is rejected with a clear error, not silently mishandled.</summary>
    public string Provider { get; set; } = "postgres";
    /// <summary>When true (default), only single SELECT statements are allowed — a pragmatic
    /// heuristic guard, not a bulletproof one. Opt in to write access per connection, explicitly.</summary>
    public bool ReadOnly { get; set; } = true;
}

public sealed class ChatMessage
{
    public required string Role { get; set; } // system | user | assistant
    public required string Content { get; set; }
}

public sealed class ConversationState
{
    public required string ConversationId { get; set; }
    public required Guid AgentId { get; set; }
    public required int AgentVersion { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One key in an agent's persistent collection (phase 9, PowerApps-inspired
/// "Collections") — a small key/value store that survives across runs and conversations,
/// unlike <see cref="ConversationState.Variables"/> which resets every new conversation. One
/// row per key rather than a single jsonb blob on <see cref="Agent"/>, so concurrent writes to
/// different keys (e.g. many chat users hitting the same published agent) never race each
/// other out — a whole-dictionary column would silently lose the loser's write on every
/// concurrent SaveChanges.</summary>
public sealed class AgentCollectionEntry
{
    public required Guid AgentId { get; set; }
    public required string Key { get; set; }
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ExecutionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ExecutionId { get; set; }
    public required string ConversationId { get; set; }
    public Guid AgentId { get; set; }
    public int AgentVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "running"; // running | completed | failed
    public string? Error { get; set; }
    public List<ExecutionStep> Steps { get; set; } = new();
}

public sealed class ExecutionStep
{
    public required string NodeId { get; set; }
    public required string NodeType { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "running";
    public string? Detail { get; set; }
    public string? Error { get; set; }
}
