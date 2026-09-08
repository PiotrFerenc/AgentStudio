namespace AgentStudio.Domain;

public sealed class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string SystemInstructions { get; set; } = "";
    public string ModelProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";

    /// <summary>Static API key protecting the published endpoints (stored as a hash).</summary>
    public string ApiKeyHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Draft is the AgentVersion with Status=Draft/Validated. Not mapped.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentVersion? Draft => Versions.FirstOrDefault(v => v.Status is AgentVersionStatus.Draft or AgentVersionStatus.Validated);

    public List<AgentVersion> Versions { get; set; } = new();
}

public enum AgentVersionStatus
{
    Draft = 0,
    Validated = 1,
    Published = 2,
    Unpublished = 3
}

public sealed class AgentVersion
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public int Version { get; set; }
    public AgentVersionStatus Status { get; set; } = AgentVersionStatus.Draft;

    /// <summary>Safety cap on total node executions per run — also the loop-iteration guard (phase 2).</summary>
    public int MaxSteps { get; set; } = 100;

    /// <summary>Serialized workflow graph JSON. Immutable once published.</summary>
    public string GraphJson { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public WorkflowGraph Graph
    {
        get => System.Text.Json.JsonSerializer.Deserialize<WorkflowGraph>(GraphJson, AgentStudioJson.Options) ?? new WorkflowGraph();
        set => GraphJson = System.Text.Json.JsonSerializer.Serialize(value, AgentStudioJson.Options);
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class WorkflowGraph
{
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowEdge> Edges { get; set; } = new();
}

public abstract class WorkflowNode
{
    public required string Id { get; set; }
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public abstract string Type { get; }
}

public sealed class StartNode : WorkflowNode
{
    public override string Type => "start";
}

public sealed class EndNode : WorkflowNode
{
    public override string Type => "end";
    /// <summary>Optional output template. Supports {variables.x} placeholders.</summary>
    public string OutputTemplate { get; set; } = "";
}

public sealed class MessageNode : WorkflowNode
{
    public override string Type => "message";
    /// <summary>Static message text appended to the assistant output. Supports {variables.x} placeholders.</summary>
    public string Text { get; set; } = "";
}

public sealed class PromptNode : WorkflowNode
{
    public override string Type => "prompt";
    public string PromptTemplate { get; set; } = "";
    /// <summary>Variable name the LLM response is written to.</summary>
    public string ResultVariable { get; set; } = "llmResult";
}

public sealed class ConditionNode : WorkflowNode
{
    public override string Type => "condition";
    /// <summary>Left side, e.g. variables.status or input</summary>
    public string Left { get; set; } = "";
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
    public string Right { get; set; } = "";
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual
}

public sealed class HttpNode : WorkflowNode
{
    public override string Type => "http";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> QueryParameters { get; set; } = new();
    public string? Body { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int Retries { get; set; } = 0;
    /// <summary>Variable name the response body is written to.</summary>
    public string ResultVariable { get; set; } = "httpResult";
}

public sealed class VariableNode : WorkflowNode
{
    public override string Type => "variable";
    public string Name { get; set; } = "";
    /// <summary>Value expression. Supports {variables.x} and {input} placeholders.</summary>
    public string Value { get; set; } = "";
}

/// <summary>Fan-out: ≥2 unconditional outgoing edges, each branch running concurrently until it
/// reaches the matching JoinNode (phase 2). See WorkflowValidator for the shape rules.</summary>
public sealed class ParallelNode : WorkflowNode
{
    public override string Type => "parallel";
}

/// <summary>Fan-in counterpart to ParallelNode — where its branches reconverge (phase 2).</summary>
public sealed class JoinNode : WorkflowNode
{
    public override string Type => "join";
}

/// <summary>RAG: embeds Query and writes the top-K most similar document chunks (of this agent's
/// uploaded documents) to ResultVariable, newline-separated (phase 2).</summary>
public sealed class DocumentSearchNode : WorkflowNode
{
    public override string Type => "documentSearch";
    /// <summary>Search query. Supports {variables.x} and {input} placeholders.</summary>
    public string Query { get; set; } = "{input}";
    public int TopK { get; set; } = 3;
    public string ResultVariable { get; set; } = "searchResult";
}

/// <summary>Calls another agent's published version as a one-shot, stateless step (phase 3) —
/// its own throwaway ConversationState, not persisted, not sharing the caller's history. The
/// full text result (buffered, not live-streamed to the caller) is written to ResultVariable.
/// Guarded against agent-to-agent cycles by WorkflowRunner's callDepth parameter, not by graph
/// validation (which agent a node targets isn't knowable without a DB round-trip).</summary>
public sealed class SubAgentNode : WorkflowNode
{
    public override string Type => "subAgent";
    public Guid TargetAgentId { get; set; }
    /// <summary>Input message for the sub-agent. Supports {variables.x} and {input} placeholders.</summary>
    public string InputTemplate { get; set; } = "{input}";
    public string ResultVariable { get; set; } = "subAgentResult";
}

/// <summary>Runs a parameterized SQL query against a named DatabaseConnectionConfig (phase 3).
/// Query is raw SQL with @name placeholders — Parameters maps each placeholder to a value
/// template (expanded via {variables.x}/{input}, then bound as a DbParameter). The Query text
/// itself is never template-expanded — that would make injection trivial. See
/// IDatabaseQueryExecutor for the read-only/SELECT-only guard and row cap.</summary>
public sealed class DatabaseQueryNode : WorkflowNode
{
    public override string Type => "databaseQuery";
    public string ConnectionName { get; set; } = "";
    /// <summary>Raw SQL with @name placeholders. Never template-expanded directly.</summary>
    public string Query { get; set; } = "";
    /// <summary>@name -> value template (expanded, then bound as a parameter — not concatenated).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public string ResultVariable { get; set; } = "dbResult";
}

public sealed class WorkflowEdge
{
    public required string Id { get; set; }
    public required string SourceNodeId { get; set; }
    public required string TargetNodeId { get; set; }
    /// <summary>For condition nodes: "true" or "false". Otherwise null.</summary>
    public string? Branch { get; set; }
}
