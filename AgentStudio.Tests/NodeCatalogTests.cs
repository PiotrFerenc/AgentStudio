using System.Reflection;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Every WorkflowNode subclass must have a NodeCatalog entry with a non-empty Summary —
/// the same "can't add a node type without wiring it up" discipline CLAUDE.md documents for the
/// JSON converter (WorkflowNodeConverter.Read) and GraphMapper.</summary>
public class NodeCatalogTests
{
    private static IEnumerable<Type> WorkflowNodeSubclasses() =>
        typeof(WorkflowNode).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(WorkflowNode).IsAssignableFrom(t));

    [Fact]
    public void Every_WorkflowNode_subclass_has_a_catalog_entry()
    {
        foreach (var type in WorkflowNodeSubclasses())
        {
            var typeString = ((WorkflowNode)Activator.CreateInstance(type, nonPublic: true)!).Type;
            var info = NodeCatalog.Get(typeString);
            Assert.True(info is not null, $"{type.Name} (Type=\"{typeString}\") has no NodeCatalog entry.");
            Assert.False(string.IsNullOrWhiteSpace(info!.Summary), $"{type.Name} has an empty NodeCatalog Summary.");
        }
    }

    [Fact]
    public void Catalog_has_no_orphaned_entries_for_types_that_no_longer_exist()
    {
        var liveTypes = WorkflowNodeSubclasses()
            .Select(t => ((WorkflowNode)Activator.CreateInstance(t, nonPublic: true)!).Type)
            .ToHashSet();
        liveTypes.Add("start"); // present in every graph but never added via the palette

        foreach (var key in NodeCatalog.Nodes.Keys)
            Assert.Contains(key, liveTypes);
    }

    [Fact]
    public void Every_param_description_is_non_empty()
    {
        foreach (var node in NodeCatalog.Nodes.Values)
            foreach (var p in node.Params)
                Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{node.Type}.{p.Key} has an empty description.");
    }
}
