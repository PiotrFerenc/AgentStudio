using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Domain;

/// <summary>Extracts a single value out of a JSON document by a small path syntax — dot-separated
/// property names with optional bracket array indices, e.g. "data.items[0].name" or "[2].id".
/// Not a full JSONPath implementation (no wildcards/filters/slices) — one value out of a known
/// response shape is the entire use case (JsonParseNode), so a small hand-rolled walker over
/// System.Text.Json is enough; a real JSONPath library would be the first new NuGet dependency
/// in this project's history for something this narrow.</summary>
public static class JsonPathExtractor
{
    private static readonly Regex TokenPattern = new(@"([^.\[\]]+)|\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>Throws InvalidOperationException with a message naming exactly what went wrong
    /// (invalid JSON, missing property, out-of-range index) — this runs inside a workflow step,
    /// same fail-clearly contract as DatabaseQueryNode/HttpNode, not a silent-null API.</summary>
    public static string Extract(string json, string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"jsonParse: input is not valid JSON ({ex.Message}).");
        }

        using (doc)
        {
            var current = doc.RootElement;
            var matches = TokenPattern.Matches(path);
            if (matches.Count == 0 && !string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException($"jsonParse: path '{path}' could not be parsed.");

            foreach (Match m in matches)
            {
                if (m.Groups[2].Success)
                {
                    var index = int.Parse(m.Groups[2].Value);
                    if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                        throw new InvalidOperationException($"jsonParse: path '{path}' — index [{index}] not found.");
                    current = current[index];
                }
                else
                {
                    var property = m.Groups[1].Value;
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out var next))
                        throw new InvalidOperationException($"jsonParse: path '{path}' — property '{property}' not found.");
                    current = next;
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? "",
                JsonValueKind.Number => current.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => current.GetRawText() // object/array — handed back as JSON text, e.g. for a further jsonParse node
            };
        }
    }
}
