using System.Reflection;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Reflects over every WorkflowNode subclass and round-trips an instance through the two
/// places CLAUDE.md says must be updated together whenever a node type is added:
/// WorkflowNodeConverter (JSON) and GraphMapper (ToDomain/ToDto). Catches a forgotten case the
/// same way NodeCatalogTests catches a missing NodeCatalog entry — before it becomes the runtime
/// deadlock CLAUDE.md documents (a missing WorkflowNodeConverter case throwing outside the
/// runner's outer try/finally), rather than after.
///
/// Deliberately not a design-pattern fix (no node-type registry) — per the code review this
/// followed from, a registry can't cover GraphMapper's hand-written Razor-adjacent Props mapping
/// without a form-metadata engine, which is a bigger abstraction than the actual problem
/// justifies. This is the cheap alternative: a test that fails loudly instead of a runtime crash.</summary>
public class NodeTypeRoundTripTests
{
    private static IEnumerable<Type> WorkflowNodeSubclasses() =>
        typeof(WorkflowNode).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(WorkflowNode).IsAssignableFrom(t));

    private static WorkflowNode NewInstance(Type type)
    {
        var node = (WorkflowNode)Activator.CreateInstance(type, nonPublic: true)!;
        node.Id = "n1";
        node.Label = "test";
        return node;
    }

    public static IEnumerable<object[]> NodeTypes() =>
        WorkflowNodeSubclasses().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void Every_node_type_round_trips_through_the_JSON_converter(Type type)
    {
        var node = NewInstance(type);

        var json = System.Text.Json.JsonSerializer.Serialize<WorkflowNode>(node, AgentStudioJson.Options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<WorkflowNode>(json, AgentStudioJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(type, roundTripped!.GetType());
        Assert.Equal(node.Type, roundTripped.Type);
    }

    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void Every_node_type_round_trips_through_GraphMapper(Type type)
    {
        var node = NewInstance(type);
        var graph = new WorkflowGraph();
        graph.Nodes.Add(node);

        var dto = GraphMapper.ToDto(graph);
        var backToDomain = GraphMapper.ToDomain(dto);

        var roundTripped = Assert.Single(backToDomain.Nodes);
        Assert.Equal(type, roundTripped.GetType());
    }

    /// <summary>Catches GraphMapper.ToDto's specific failure mode: its switch has no default
    /// arm, so a node type missing its case doesn't throw — it silently serializes an empty
    /// Props dict. A type with real properties (anything beyond the Id/Label/X/Y/Type every
    /// WorkflowNode already has) must come back with at least one Props entry; a type with
    /// genuinely no properties of its own (start/parallel/join) is correctly exempt.</summary>
    [Theory]
    [MemberData(nameof(NodeTypes))]
    public void Node_types_with_their_own_properties_produce_non_empty_props_in_ToDto(Type type)
    {
        // DeclaredOnly still includes the "Type" override every subclass provides (it's an
        // abstract member re-declared per type, not inherited) — exclude it by name, it's not a
        // Props-mappable field.
        var ownProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.Name != nameof(WorkflowNode.Type));
        if (!ownProperties.Any()) return; // e.g. StartNode/ParallelNode/JoinNode — nothing to lose

        var node = NewInstance(type);
        var graph = new WorkflowGraph();
        graph.Nodes.Add(node);

        var dto = GraphMapper.ToDto(graph);

        Assert.NotEmpty(Assert.Single(dto.Nodes).Props);
    }
}
