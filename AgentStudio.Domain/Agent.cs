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

    /// <summary>Serialized <see cref="FormField"/> list JSON (phase 3, forms) — same
    /// serialize-on-set pattern as <see cref="GraphJson"/>/<see cref="Graph"/>.</summary>
    public string FormFieldsJson { get; set; } = "[]";

    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<FormField> FormFields
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FormFieldsJson)) return new List<FormField>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<FormField>>(FormFieldsJson, AgentStudioJson.Options) ?? new List<FormField>();
            }
            catch (System.Text.Json.JsonException)
            {
                // A pre-migration/legacy row can hold something that isn't a JSON array (e.g. a
                // Postgres jsonb column's own default coercion). Treat as "no fields" rather than
                // crashing every page that loads this version.
                return new List<FormField>();
            }
        }
        set => FormFieldsJson = System.Text.Json.JsonSerializer.Serialize(value, AgentStudioJson.Options);
    }

    /// <summary>What happens with a form run's result: "inline" (default, shown on the page),
    /// "redirect" (browser navigates to FormResultTarget) or "webhook" (server POSTs the result
    /// to FormResultTarget, then still shows a confirmation — a webhook target isn't necessarily
    /// meant for the browser, but the visitor still needs to see the submission succeeded).</summary>
    public string FormResultMode { get; set; } = "inline";

    /// <summary>URL for "redirect"/"webhook" modes. Supports {result}/{conversationId}/
    /// {executionId} placeholders, expanded the same way workflow templates are.</summary>
    public string? FormResultTarget { get; set; }

    /// <summary>Renders an inline result as Markdown (a small safe subset, not full CommonMark —
    /// see MinimalMarkdown) instead of plain text. Ignored for "redirect"/"webhook".</summary>
    public bool FormResultMarkdown { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>A single input field of an agent's published form (phase 3) — an alternative,
/// one-shot way to run an agent besides chat. Values become <c>{variables.Name}</c> in the
/// graph, just like any other conversation variable.</summary>
public sealed class FormField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>text | number | textarea | select | checkbox | date | email | url.</summary>
    public string Type { get; set; } = "text";

    /// <summary>Choices for Type == "select".</summary>
    public List<string> Options { get; set; } = new();
    public bool Required { get; set; }

    public string? DefaultValue { get; set; }
    public string? Placeholder { get; set; }
    public string? HelpText { get; set; }

    /// <summary>Text length bounds (text/textarea/email/url). Null = unbounded.</summary>
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>Numeric bounds (Type == "number"). Null = unbounded.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Optional regex the value must match (text/email/url). Validated server-side in
    /// FormFieldValidator — never trust a client-side-only check for a public endpoint.</summary>
    public string? Pattern { get; set; }

    /// <summary>Shown instead of a generic message when Pattern/Min/Max/Length validation fails.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Fields sharing a non-null GroupName render under one section header, in field
    /// order — no separate "sections" collection, the existing field list already carries order.</summary>
    public string? GroupName { get; set; }

    /// <summary>When set, this field is only shown/required/validated while the field named
    /// VisibleWhenField currently equals VisibleWhenEquals — both null means always visible.</summary>
    public string? VisibleWhenField { get; set; }
    public string? VisibleWhenEquals { get; set; }

    /// <summary>1-based wizard page number. All fields defaulting to 1 renders as today's
    /// single-page form; using 2+ turns the form into a multi-step wizard.</summary>
    public int Step { get; set; } = 1;
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

/// <summary>Extracts a single value out of a JSON blob (e.g. an HttpNode's response) by a small
/// dot/bracket path — "data.items[0].name". See JsonPathExtractor for the path syntax and
/// exact error behavior. Input is template-expanded (e.g. "{variables.httpResult}"); Path is
/// not — it's a fixed path into a known response shape, not something a caller should be able
/// to redirect at runtime.</summary>
public sealed class JsonParseNode : WorkflowNode
{
    public override string Type => "jsonParse";
    public string Input { get; set; } = "{input}";
    public string Path { get; set; } = "";
    public string ResultVariable { get; set; } = "jsonResult";
}

/// <summary>Computes one value with a small spreadsheet-like formula (phase 8) — arithmetic,
/// comparisons, IF/CONCAT/LEN/etc — instead of chaining condition+variable nodes for something
/// that's really one expression. See <see cref="FormulaEvaluator"/> for the grammar/functions.</summary>
public sealed class ExpressionNode : WorkflowNode
{
    public override string Type => "expression";
    public string Formula { get; set; } = "";
    public string ResultVariable { get; set; } = "result";
}

/// <summary>Reads one key from the agent's persistent collection (phase 9) — survives across
/// runs/conversations, unlike a normal workflow variable. <see cref="DefaultValue"/> (template-
/// expanded, like <see cref="Key"/>) is used when the key has never been set.</summary>
public sealed class CollectionGetNode : WorkflowNode
{
    public override string Type => "collectionGet";
    public string Key { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string ResultVariable { get; set; } = "collectionResult";
}

/// <summary>Writes one key to the agent's persistent collection (phase 9). Both <see cref="Key"/>
/// and <see cref="Value"/> are template-expanded.</summary>
public sealed class CollectionSetNode : WorkflowNode
{
    public override string Type => "collectionSet";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "{input}";
}

/// <summary>Calls a registered IIntegrator by name (phase 4) — the extension point for custom
/// integrations (GitLab, Jira, ...). Config values support {variables.x}/{input} placeholders,
/// expanded before being handed to the integrator — same rigor as DatabaseQueryNode.Parameters.
/// New integrators are written as code (IIntegrator implementations, DI-registered), not
/// configured through this node — the node only selects one by name and supplies per-call
/// values.</summary>
public sealed class IntegratorNode : WorkflowNode
{
    public override string Type => "integrator";
    public string IntegratorName { get; set; } = "";
    public Dictionary<string, string> Config { get; set; } = new();
    public string ResultVariable { get; set; } = "integratorResult";
}

public sealed class WorkflowEdge
{
    public required string Id { get; set; }
    public required string SourceNodeId { get; set; }
    public required string TargetNodeId { get; set; }
    /// <summary>For condition nodes: "true" or "false". Otherwise null.</summary>
    public string? Branch { get; set; }
}
