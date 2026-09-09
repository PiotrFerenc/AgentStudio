using AgentStudio.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public class GraphDiffTests
{
    private static WorkflowNodeDto Node(string id, string type, string label = "", Dictionary<string, object?>? props = null) =>
        new() { Id = id, Type = type, Label = label, Props = props ?? new() };

    private static WorkflowEdgeDto Edge(string id, string from, string to, string? branch = null) =>
        new() { Id = id, SourceNodeId = from, TargetNodeId = to, Branch = branch };

    [Fact]
    public void No_differences_between_identical_graphs()
    {
        var a = new WorkflowGraphDto { Nodes = { Node("s", "start") }, Edges = { } };
        var b = new WorkflowGraphDto { Nodes = { Node("s", "start") }, Edges = { } };

        var diff = GraphDiff.Compare(a, b);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Detects_added_and_removed_nodes()
    {
        var a = new WorkflowGraphDto { Nodes = { Node("s", "start"), Node("v", "variable") } };
        var b = new WorkflowGraphDto { Nodes = { Node("s", "start"), Node("e", "end") } };

        var diff = GraphDiff.Compare(a, b);

        Assert.Contains(diff.Nodes, n => n.NodeId == "v" && n.ChangeKind == "removed");
        Assert.Contains(diff.Nodes, n => n.NodeId == "e" && n.ChangeKind == "added");
        Assert.DoesNotContain(diff.Nodes, n => n.NodeId == "s");
    }

    [Fact]
    public void Detects_modified_node_props_and_label()
    {
        var a = new WorkflowGraphDto { Nodes = { Node("j", "jsonParse", "old", new() { ["path"] = "a.b" }) } };
        var b = new WorkflowGraphDto { Nodes = { Node("j", "jsonParse", "new", new() { ["path"] = "a.c" }) } };

        var diff = GraphDiff.Compare(a, b);

        var entry = Assert.Single(diff.Nodes);
        Assert.Equal("modified", entry.ChangeKind);
        Assert.Contains("label", entry.ChangedProps);
        Assert.Contains("path", entry.ChangedProps);
    }

    [Fact]
    public void Unchanged_props_are_not_reported()
    {
        var a = new WorkflowGraphDto { Nodes = { Node("j", "jsonParse", props: new() { ["path"] = "a.b" }) } };
        var b = new WorkflowGraphDto { Nodes = { Node("j", "jsonParse", props: new() { ["path"] = "a.b" }) } };

        var diff = GraphDiff.Compare(a, b);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Tolerates_duplicate_edge_ids_instead_of_throwing()
    {
        // Real hand-edited graphs aren't guaranteed unique edge Ids — the engine looks edges up
        // by SourceNodeId+Branch, never by Id, so nothing enforces uniqueness.
        var a = new WorkflowGraphDto { Edges = { Edge("e103", "s", "v"), Edge("e103", "v", "e") } };
        var b = new WorkflowGraphDto { Edges = { Edge("e103", "s", "v"), Edge("e103", "v", "e") } };

        var diff = GraphDiff.Compare(a, b);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Detects_added_removed_and_modified_edges()
    {
        var a = new WorkflowGraphDto { Edges = { Edge("1", "s", "v"), Edge("2", "v", "e") } };
        var b = new WorkflowGraphDto { Edges = { Edge("1", "s", "v"), Edge("2", "v", "j"), Edge("3", "j", "e") } };

        var diff = GraphDiff.Compare(a, b);

        Assert.Contains(diff.Edges, e => e.EdgeId == "2" && e.ChangeKind == "modified" && e.TargetNodeId == "j");
        Assert.Contains(diff.Edges, e => e.EdgeId == "3" && e.ChangeKind == "added");
        Assert.DoesNotContain(diff.Edges, e => e.EdgeId == "1");
    }
}
