namespace AgentStudio.Contracts;

public sealed record NodeDiffEntry(string NodeId, string ChangeKind, string? Type, string? Label, List<string> ChangedProps);
public sealed record EdgeDiffEntry(string EdgeId, string ChangeKind, string? SourceNodeId, string? TargetNodeId, string? Branch);
public sealed record GraphDiffResult(List<NodeDiffEntry> Nodes, List<EdgeDiffEntry> Edges)
{
    public bool IsEmpty => Nodes.Count == 0 && Edges.Count == 0;
}

/// <summary>Pure/static, same spirit as <c>ConditionEvaluator</c>/<c>FormFieldValidator</c> — no
/// DI, just a structural comparison of two graphs' DTOs (nodes matched by Id, edges by Id).
/// Operates on <see cref="WorkflowNodeDto.Props"/> rather than typed <c>WorkflowNode</c>
/// subclasses so one comparison works for every node type without a per-type case.</summary>
public static class GraphDiff
{
    public static GraphDiffResult Compare(WorkflowGraphDto from, WorkflowGraphDto to)
    {
        var nodeDiffs = new List<NodeDiffEntry>();
        var oldNodes = ById(from.Nodes, n => n.Id);
        var newNodes = ById(to.Nodes, n => n.Id);

        foreach (var id in oldNodes.Keys.Except(newNodes.Keys))
            nodeDiffs.Add(new NodeDiffEntry(id, "removed", oldNodes[id].Type, oldNodes[id].Label, new()));
        foreach (var id in newNodes.Keys.Except(oldNodes.Keys))
            nodeDiffs.Add(new NodeDiffEntry(id, "added", newNodes[id].Type, newNodes[id].Label, new()));
        foreach (var id in oldNodes.Keys.Intersect(newNodes.Keys))
        {
            var a = oldNodes[id];
            var b = newNodes[id];
            var changed = new List<string>();
            if (a.Label != b.Label) changed.Add("label");
            if (a.Type != b.Type) changed.Add("type");
            foreach (var key in a.Props.Keys.Union(b.Props.Keys))
                if (PropText(a.Props, key) != PropText(b.Props, key))
                    changed.Add(key);
            if (changed.Count > 0)
                nodeDiffs.Add(new NodeDiffEntry(id, "modified", b.Type, b.Label, changed));
        }

        var edgeDiffs = new List<EdgeDiffEntry>();
        var oldEdges = ById(from.Edges, e => e.Id);
        var newEdges = ById(to.Edges, e => e.Id);

        foreach (var id in oldEdges.Keys.Except(newEdges.Keys))
            edgeDiffs.Add(new EdgeDiffEntry(id, "removed", oldEdges[id].SourceNodeId, oldEdges[id].TargetNodeId, oldEdges[id].Branch));
        foreach (var id in newEdges.Keys.Except(oldEdges.Keys))
            edgeDiffs.Add(new EdgeDiffEntry(id, "added", newEdges[id].SourceNodeId, newEdges[id].TargetNodeId, newEdges[id].Branch));
        foreach (var id in oldEdges.Keys.Intersect(newEdges.Keys))
        {
            var a = oldEdges[id];
            var b = newEdges[id];
            if (a.SourceNodeId != b.SourceNodeId || a.TargetNodeId != b.TargetNodeId || a.Branch != b.Branch)
                edgeDiffs.Add(new EdgeDiffEntry(id, "modified", b.SourceNodeId, b.TargetNodeId, b.Branch));
        }

        return new GraphDiffResult(nodeDiffs, edgeDiffs);
    }

    /// <summary>Tolerates a duplicate Id (a pre-existing/hand-edited graph isn't guaranteed
    /// unique Ids — the engine itself never looks nodes/edges up by Id, only by
    /// SourceNodeId+Branch, so nothing enforces it) instead of throwing like a plain
    /// ToDictionary would. Last one wins, same "don't crash on messy real data" contract as
    /// the jsonb-column getters elsewhere in this project.</summary>
    private static Dictionary<string, T> ById<T>(IEnumerable<T> items, Func<T, string> id)
    {
        var dict = new Dictionary<string, T>();
        foreach (var item in items)
            dict[id(item)] = item;
        return dict;
    }

    private static string PropText(Dictionary<string, object?> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return "";
        if (v is System.Text.Json.JsonElement je) return je.GetRawText();
        if (v is Dictionary<string, string> d) return string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        return v.ToString() ?? "";
    }
}
