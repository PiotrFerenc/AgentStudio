using AgentStudio.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public class GraphComponentInserterTests
{
    private static WorkflowNodeDto Node(string id, string type, double x, double y, Dictionary<string, object?>? props = null) =>
        new() { Id = id, Type = type, X = x, Y = y, Props = props ?? new() };

    private static WorkflowEdgeDto Edge(string id, string from, string to, string? branch = null) =>
        new() { Id = id, SourceNodeId = from, TargetNodeId = to, Branch = branch };

    private static WorkflowGraphDto TwoNodeComponent() => new()
    {
        Nodes = { Node("a", "message", 100, 200), Node("b", "message", 150, 250) },
        Edges = { Edge("e1", "a", "b") }
    };

    [Fact]
    public void Clone_generates_fresh_ids_via_the_supplied_generator()
    {
        var counter = 0;
        var (nodes, edges) = GraphComponentInserter.Clone(TwoNodeComponent(), type => $"{type}{++counter}", () => $"edge{++counter}");

        Assert.Equal(2, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.Id is "a" or "b");
        Assert.Single(edges);
        Assert.DoesNotContain(edges, e => e.Id == "e1");
    }

    [Fact]
    public void Clone_remaps_edge_endpoints_to_the_new_node_ids()
    {
        var (nodes, edges) = GraphComponentInserter.Clone(TwoNodeComponent(), type => $"new-{type}-{Guid.NewGuid():N}", () => $"new-edge-{Guid.NewGuid():N}");

        var edge = Assert.Single(edges);
        Assert.Contains(nodes, n => n.Id == edge.SourceNodeId);
        Assert.Contains(nodes, n => n.Id == edge.TargetNodeId);
    }

    [Fact]
    public void Clone_normalizes_positions_relative_to_the_components_own_bounding_box()
    {
        var (nodes, _) = GraphComponentInserter.Clone(TwoNodeComponent(), type => type, () => "e");

        // Original min was (100,200) — after normalization the top-left-most node sits at (0,0).
        Assert.Contains(nodes, n => n.X == 0 && n.Y == 0);
        Assert.Contains(nodes, n => n.X == 50 && n.Y == 50);
    }

    [Fact]
    public void Clone_deep_copies_nested_dictionary_props_so_two_clones_never_share_state()
    {
        var component = new WorkflowGraphDto
        {
            Nodes = { Node("a", "databaseQuery", 0, 0, new() { ["parameters"] = new Dictionary<string, string> { ["id"] = "1" } }) }
        };

        var (firstClone, _) = GraphComponentInserter.Clone(component, type => "first", () => "e1");
        var (secondClone, _) = GraphComponentInserter.Clone(component, type => "second", () => "e2");

        var firstParams = (Dictionary<string, string>)firstClone[0].Props["parameters"]!;
        var secondParams = (Dictionary<string, string>)secondClone[0].Props["parameters"]!;
        firstParams["id"] = "mutated";

        Assert.Equal("1", secondParams["id"]); // unaffected by mutating the first clone
    }

    [Fact]
    public void Clone_of_an_empty_component_returns_empty_lists()
    {
        var (nodes, edges) = GraphComponentInserter.Clone(new WorkflowGraphDto(), type => type, () => "e");

        Assert.Empty(nodes);
        Assert.Empty(edges);
    }

    [Fact]
    public void Two_clones_of_the_same_component_never_collide_on_id()
    {
        var counter = 0;
        var (firstNodes, firstEdges) = GraphComponentInserter.Clone(TwoNodeComponent(), type => $"{type}{++counter}", () => $"e{++counter}");
        var (secondNodes, secondEdges) = GraphComponentInserter.Clone(TwoNodeComponent(), type => $"{type}{++counter}", () => $"e{++counter}");

        var allNodeIds = firstNodes.Concat(secondNodes).Select(n => n.Id).ToList();
        var allEdgeIds = firstEdges.Concat(secondEdges).Select(e => e.Id).ToList();
        Assert.Equal(allNodeIds.Count, allNodeIds.Distinct().Count());
        Assert.Equal(allEdgeIds.Count, allEdgeIds.Distinct().Count());
    }
}
