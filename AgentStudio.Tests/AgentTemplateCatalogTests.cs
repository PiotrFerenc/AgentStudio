using AgentStudio.Contracts;
using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Every built-in AgentTemplate's Graph must pass WorkflowValidator with zero errors —
/// catches a template silently rotting (e.g. a node's prop key renamed elsewhere) before it
/// ships a broken starter graph to a user. Same catalog-integrity discipline as
/// NodeCatalogTests.</summary>
public class AgentTemplateCatalogTests
{
    [Theory]
    [MemberData(nameof(Templates))]
    public void Template_graph_passes_workflow_validation(AgentTemplate template)
    {
        var domain = GraphMapper.ToDomain(template.Graph);
        var errors = WorkflowValidator.Validate(domain);

        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Template_has_a_name_and_description(AgentTemplate template)
    {
        Assert.False(string.IsNullOrWhiteSpace(template.Name));
        Assert.False(string.IsNullOrWhiteSpace(template.Description));
    }

    public static IEnumerable<object[]> Templates() =>
        AgentTemplateCatalog.Templates.Select(t => new object[] { t });

    [Fact]
    public void Get_unknown_id_returns_null()
    {
        Assert.Null(AgentTemplateCatalog.Get("not-a-real-template"));
    }

    [Fact]
    public void Get_null_id_returns_null()
    {
        Assert.Null(AgentTemplateCatalog.Get(null));
    }

    [Fact]
    public void Ids_are_unique()
    {
        var ids = AgentTemplateCatalog.Templates.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
