using AgentStudio.Domain;

namespace AgentStudio.Contracts;

public sealed record CreateAgentRequest(string Name, string Description, string SystemInstructions, string ModelProviderName, string ModelName);
public sealed record UpdateDraftRequest(
    WorkflowGraphDto Graph,
    int? MaxSteps = null,
    List<FormField>? FormFields = null,
    string? FormResultMode = null,
    string? FormResultTarget = null,
    bool? FormResultMarkdown = null);
public sealed record AgentDto(Guid Id, string Name, string Description, string SystemInstructions, string ModelProviderName, string ModelName, DateTimeOffset CreatedAt);
public sealed record AgentVersionDto(int Version, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int MaxSteps);
public sealed record ValidationResultDto(bool IsValid, List<string> Errors);
public sealed record ConversationRequest(string Message, string? ConversationId);
public sealed record ConversationResponse(string ConversationId, string Reply, string ExecutionId);
public sealed record ProviderDto(Guid Id, string Name, string BaseUrl, string DefaultModel, bool HasApiKey, string? EmbeddingModel, Dictionary<string, string> Headers);
public sealed record ExecutionLogDto(string ExecutionId, string ConversationId, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error, List<ExecutionStepDto> Steps);
public sealed record ExecutionStepDto(string NodeId, string NodeType, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Detail, string? Error);
public sealed record DocumentDto(Guid Id, string FileName, DateTimeOffset CreatedAt, int ChunkCount);
public sealed record GraphComponentSummary(Guid Id, string Name, string Description, DateTimeOffset CreatedAt);
/// <summary>Name+description only — never the IIntegrator instance itself, so a Blazor
/// component never holds a reference to the executable service.</summary>
public sealed record IntegratorSummary(string Name, string Description);

public sealed class WorkflowGraphDto
{
    public List<WorkflowNodeDto> Nodes { get; set; } = new();
    public List<WorkflowEdgeDto> Edges { get; set; } = new();
}

public sealed class WorkflowNodeDto
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, object?> Props { get; set; } = new();
}

public sealed class WorkflowEdgeDto
{
    public required string Id { get; set; }
    public required string SourceNodeId { get; set; }
    public required string TargetNodeId { get; set; }
    public string? Branch { get; set; }
}

public static class GraphMapper
{
    public static WorkflowGraph ToDomain(WorkflowGraphDto dto)
    {
        var graph = new WorkflowGraph();
        foreach (var n in dto.Nodes)
        {
            WorkflowNode node = n.Type switch
            {
                "start" => new StartNode { Id = n.Id },
                "end" => new EndNode { Id = n.Id, OutputTemplate = Prop(n, "outputTemplate") },
                "message" => new MessageNode { Id = n.Id, Text = Prop(n, "text"), ResultVariable = Prop(n, "resultVariable", "messageResult") },
                "prompt" => new PromptNode { Id = n.Id, PromptTemplate = Prop(n, "promptTemplate"), ResultVariable = Prop(n, "resultVariable", "llmResult"), ProviderName = Prop(n, "providerName"), ModelName = Prop(n, "modelName") },
                "condition" => new ConditionNode { Id = n.Id, Left = Prop(n, "left"), Right = Prop(n, "right"), Operator = ParseOperator(Prop(n, "operator", "Equals")) },
                "http" => new HttpNode
                {
                    Id = n.Id,
                    Method = Prop(n, "method", "GET"),
                    Url = Prop(n, "url"),
                    Body = OptProp(n, "body"),
                    TimeoutSeconds = PropInt(n, "timeoutSeconds", 30),
                    Retries = PropInt(n, "retries", 0),
                    ResultVariable = Prop(n, "resultVariable", "httpResult"),
                    Headers = PropDict(n, "headers"),
                    QueryParameters = PropDict(n, "queryParameters")
                },
                "variable" => new VariableNode { Id = n.Id, Name = Prop(n, "name"), Value = Prop(n, "value") },
                "parallel" => new ParallelNode { Id = n.Id },
                "join" => new JoinNode { Id = n.Id },
                "documentSearch" => new DocumentSearchNode { Id = n.Id, Query = Prop(n, "query", "{input}"), TopK = PropInt(n, "topK", 3), ResultVariable = Prop(n, "resultVariable", "searchResult"), ProviderName = Prop(n, "providerName") },
                "subAgent" => new SubAgentNode { Id = n.Id, TargetAgentId = PropGuid(n, "targetAgentId"), InputTemplate = Prop(n, "inputTemplate", "{input}"), ResultVariable = Prop(n, "resultVariable", "subAgentResult") },
                "databaseQuery" => new DatabaseQueryNode
                {
                    Id = n.Id,
                    ConnectionName = Prop(n, "connectionName"),
                    Query = Prop(n, "query"),
                    Parameters = PropDict(n, "parameters"),
                    TimeoutSeconds = PropInt(n, "timeoutSeconds", 30),
                    ResultVariable = Prop(n, "resultVariable", "dbResult")
                },
                "integrator" => new IntegratorNode
                {
                    Id = n.Id,
                    IntegratorName = Prop(n, "integratorName"),
                    Config = PropDict(n, "config"),
                    ResultVariable = Prop(n, "resultVariable", "integratorResult")
                },
                "jsonParse" => new JsonParseNode
                {
                    Id = n.Id,
                    Input = Prop(n, "input", "{input}"),
                    Path = Prop(n, "path"),
                    ResultVariable = Prop(n, "resultVariable", "jsonResult")
                },
                "expression" => new ExpressionNode
                {
                    Id = n.Id,
                    Formula = Prop(n, "formula"),
                    ResultVariable = Prop(n, "resultVariable", "result")
                },
                "collectionGet" => new CollectionGetNode
                {
                    Id = n.Id,
                    Key = Prop(n, "key"),
                    DefaultValue = Prop(n, "defaultValue"),
                    ResultVariable = Prop(n, "resultVariable", "collectionResult")
                },
                "collectionSet" => new CollectionSetNode
                {
                    Id = n.Id,
                    Key = Prop(n, "key"),
                    Value = Prop(n, "value", "{input}")
                },
                _ => throw new InvalidOperationException($"Unknown node type: {n.Type}")
            };
            node.Label = n.Label;
            node.X = n.X;
            node.Y = n.Y;
            graph.Nodes.Add(node);
        }

        foreach (var e in dto.Edges)
            graph.Edges.Add(new WorkflowEdge { Id = e.Id, SourceNodeId = e.SourceNodeId, TargetNodeId = e.TargetNodeId, Branch = e.Branch });

        return graph;
    }

    public static WorkflowGraphDto ToDto(WorkflowGraph graph)
    {
        var dto = new WorkflowGraphDto();
        foreach (var node in graph.Nodes)
        {
            var n = new WorkflowNodeDto { Id = node.Id, Type = node.Type, Label = node.Label, X = node.X, Y = node.Y };
            switch (node)
            {
                case EndNode e: n.Props["outputTemplate"] = e.OutputTemplate; break;
                case MessageNode m: n.Props["text"] = m.Text; n.Props["resultVariable"] = m.ResultVariable; break;
                case PromptNode p:
                    n.Props["promptTemplate"] = p.PromptTemplate; n.Props["resultVariable"] = p.ResultVariable;
                    n.Props["providerName"] = p.ProviderName; n.Props["modelName"] = p.ModelName;
                    break;
                case ConditionNode c: n.Props["left"] = c.Left; n.Props["right"] = c.Right; n.Props["operator"] = c.Operator.ToString(); break;
                case HttpNode h:
                    n.Props["method"] = h.Method; n.Props["url"] = h.Url; n.Props["body"] = h.Body;
                    n.Props["timeoutSeconds"] = h.TimeoutSeconds; n.Props["retries"] = h.Retries;
                    n.Props["resultVariable"] = h.ResultVariable;
                    n.Props["headers"] = h.Headers; n.Props["queryParameters"] = h.QueryParameters;
                    break;
                case VariableNode v: n.Props["name"] = v.Name; n.Props["value"] = v.Value; break;
                case DocumentSearchNode d:
                    n.Props["query"] = d.Query; n.Props["topK"] = d.TopK; n.Props["resultVariable"] = d.ResultVariable;
                    n.Props["providerName"] = d.ProviderName;
                    break;
                case SubAgentNode s: n.Props["targetAgentId"] = s.TargetAgentId.ToString(); n.Props["inputTemplate"] = s.InputTemplate; n.Props["resultVariable"] = s.ResultVariable; break;
                case DatabaseQueryNode q:
                    n.Props["connectionName"] = q.ConnectionName; n.Props["query"] = q.Query;
                    n.Props["timeoutSeconds"] = q.TimeoutSeconds; n.Props["resultVariable"] = q.ResultVariable;
                    n.Props["parameters"] = q.Parameters;
                    break;
                case IntegratorNode i:
                    n.Props["integratorName"] = i.IntegratorName; n.Props["resultVariable"] = i.ResultVariable;
                    n.Props["config"] = i.Config;
                    break;
                case JsonParseNode j:
                    n.Props["input"] = j.Input; n.Props["path"] = j.Path; n.Props["resultVariable"] = j.ResultVariable;
                    break;
                case ExpressionNode x:
                    n.Props["formula"] = x.Formula; n.Props["resultVariable"] = x.ResultVariable;
                    break;
                case CollectionGetNode cg:
                    n.Props["key"] = cg.Key; n.Props["defaultValue"] = cg.DefaultValue; n.Props["resultVariable"] = cg.ResultVariable;
                    break;
                case CollectionSetNode cs:
                    n.Props["key"] = cs.Key; n.Props["value"] = cs.Value;
                    break;
            }
            dto.Nodes.Add(n);
        }

        foreach (var e in graph.Edges)
            dto.Edges.Add(new WorkflowEdgeDto { Id = e.Id, SourceNodeId = e.SourceNodeId, TargetNodeId = e.TargetNodeId, Branch = e.Branch });

        return dto;
    }

    private static string Prop(WorkflowNodeDto n, string key, string fallback = "") =>
        n.Props.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? fallback : fallback;

    private static string? OptProp(WorkflowNodeDto n, string key) =>
        n.Props.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int PropInt(WorkflowNodeDto n, string key, int fallback) =>
        n.Props.TryGetValue(key, out var v) && int.TryParse(v?.ToString(), out var i) ? i : fallback;

    private static Guid PropGuid(WorkflowNodeDto n, string key) =>
        n.Props.TryGetValue(key, out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : Guid.Empty;

    private static Dictionary<string, string> PropDict(WorkflowNodeDto n, string key)
    {
        if (!n.Props.TryGetValue(key, out var v) || v is null) return new();
        if (v is Dictionary<string, string> d) return d;
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>();
            foreach (var p in je.EnumerateObject())
                result[p.Name] = p.Value.ToString();
            return result;
        }
        return new();
    }

    private static ConditionOperator ParseOperator(string op) =>
        Enum.TryParse<ConditionOperator>(op, ignoreCase: true, out var result) ? result : ConditionOperator.Equals;
}
