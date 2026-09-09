namespace AgentStudio.Contracts;

/// <summary>Clones a saved graph component's nodes/edges with fresh ids so it can be pasted
/// into a graph — including a second time into the same graph — without id collisions. Pure/
/// static, same spirit as <c>GraphDiff</c>: the graph editor supplies an id generator and does
/// the actual insertion; this only computes what to insert.</summary>
public static class GraphComponentInserter
{
    /// <summary>Nodes come back repositioned relative to (0,0) — the component's own bounding
    /// box, not wherever it originally sat in its source graph — so the caller can place them
    /// anywhere by adding a single offset, without needing to know the original layout.</summary>
    public static (List<WorkflowNodeDto> Nodes, List<WorkflowEdgeDto> Edges) Clone(
        WorkflowGraphDto component, Func<string, string> newNodeId, Func<string> newEdgeId)
    {
        if (component.Nodes.Count == 0)
            return (new List<WorkflowNodeDto>(), new List<WorkflowEdgeDto>());

        var idMap = new Dictionary<string, string>();
        var minX = component.Nodes.Min(n => n.X);
        var minY = component.Nodes.Min(n => n.Y);

        var nodes = new List<WorkflowNodeDto>();
        foreach (var n in component.Nodes)
        {
            var id = newNodeId(n.Type);
            idMap[n.Id] = id;
            nodes.Add(new WorkflowNodeDto
            {
                Id = id,
                Type = n.Type,
                Label = n.Label,
                X = n.X - minX,
                Y = n.Y - minY,
                Props = CloneProps(n.Props)
            });
        }

        var edges = component.Edges.Select(e => new WorkflowEdgeDto
        {
            Id = newEdgeId(),
            SourceNodeId = idMap[e.SourceNodeId],
            TargetNodeId = idMap[e.TargetNodeId],
            Branch = e.Branch
        }).ToList();

        return (nodes, edges);
    }

    /// <summary>Deep-copies any nested Dictionary&lt;string,string&gt; prop value
    /// (Parameters/Config/Headers) — otherwise two clones of the same component would share the
    /// same dictionary instance, and editing one node's Parameters would silently corrupt the
    /// other. Other prop value shapes (string/int/etc) already copy by value.</summary>
    private static Dictionary<string, object?> CloneProps(Dictionary<string, object?> props) =>
        props.ToDictionary(kv => kv.Key, kv => kv.Value is Dictionary<string, string> d ? (object?)new Dictionary<string, string>(d) : kv.Value);
}
