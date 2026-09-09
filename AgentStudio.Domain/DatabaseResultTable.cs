using System.Text.Json;

namespace AgentStudio.Domain;

/// <summary>Recognizes a <c>DatabaseQueryNode</c> result's own JSON envelope
/// (<c>{"rows":[...],"rowCount":N,"truncated":bool}</c>, see <c>DatabaseQueryExecutor</c>) so a
/// form whose End node outputs a raw databaseQuery result can render as a table instead of a
/// JSON blob. Anything else — including a jsonParse'd single value — isn't recognized, the
/// caller falls back to plain-text/markdown rendering.</summary>
public static class DatabaseResultTable
{
    public static bool TryParse(string json, out List<string> columns, out List<Dictionary<string, string>> rows, out bool truncated)
    {
        columns = new();
        rows = new();
        truncated = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array) return false;
            if (!doc.RootElement.TryGetProperty("rowCount", out _)) return false;
            if (doc.RootElement.TryGetProperty("truncated", out var tEl) &&
                tEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                truncated = tEl.GetBoolean();

            var colSet = new List<string>();
            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                if (rowEl.ValueKind != JsonValueKind.Object) return false;
                var row = new Dictionary<string, string>();
                foreach (var prop in rowEl.EnumerateObject())
                {
                    if (!colSet.Contains(prop.Name)) colSet.Add(prop.Name);
                    row[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Null => "",
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        _ => prop.Value.GetRawText()
                    };
                }
                rows.Add(row);
            }
            columns = colSet;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
