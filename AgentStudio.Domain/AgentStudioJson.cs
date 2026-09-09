using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Domain;

public static class AgentStudioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new WorkflowNodeConverter() }
    };
}

/// <summary>Polymorphic converter for WorkflowNode based on the "type" discriminator.</summary>
public sealed class WorkflowNodeConverter : JsonConverter<WorkflowNode>
{
    public override WorkflowNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString()
            : root.TryGetProperty("Type", out var t2) ? t2.GetString()
            : throw new JsonException("Missing node type discriminator.");
        var raw = root.GetRawText();
        return type switch
        {
            "start" => JsonSerializer.Deserialize<StartNode>(raw, options)!,
            "end" => JsonSerializer.Deserialize<EndNode>(raw, options)!,
            "message" => JsonSerializer.Deserialize<MessageNode>(raw, options)!,
            "prompt" => JsonSerializer.Deserialize<PromptNode>(raw, options)!,
            "condition" => JsonSerializer.Deserialize<ConditionNode>(raw, options)!,
            "http" => JsonSerializer.Deserialize<HttpNode>(raw, options)!,
            "variable" => JsonSerializer.Deserialize<VariableNode>(raw, options)!,
            "parallel" => JsonSerializer.Deserialize<ParallelNode>(raw, options)!,
            "join" => JsonSerializer.Deserialize<JoinNode>(raw, options)!,
            "documentSearch" => JsonSerializer.Deserialize<DocumentSearchNode>(raw, options)!,
            "subAgent" => JsonSerializer.Deserialize<SubAgentNode>(raw, options)!,
            "databaseQuery" => JsonSerializer.Deserialize<DatabaseQueryNode>(raw, options)!,
            "integrator" => JsonSerializer.Deserialize<IntegratorNode>(raw, options)!,
            "jsonParse" => JsonSerializer.Deserialize<JsonParseNode>(raw, options)!,
            "expression" => JsonSerializer.Deserialize<ExpressionNode>(raw, options)!,
            "collectionGet" => JsonSerializer.Deserialize<CollectionGetNode>(raw, options)!,
            "collectionSet" => JsonSerializer.Deserialize<CollectionSetNode>(raw, options)!,
            _ => throw new JsonException($"Unknown node type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, WorkflowNode value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
