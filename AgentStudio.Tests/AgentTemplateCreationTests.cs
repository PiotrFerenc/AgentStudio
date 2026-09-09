using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AgentService.CreateAsync's optional AgentTemplate parameter — the studio's "New from
/// template" flow (Home.razor).</summary>
public class AgentTemplateCreationTests
{
    private static AgentService NewService(string dbName) =>
        new(new AgentRepository(new AgentStudioDbContext(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options)),
            new ApiKeyService(),
            new UserRepository(Options.Create(new List<UserConfig>())));

    [Fact]
    public async Task Without_a_template_seeds_the_default_start_prompt_end_graph()
    {
        var service = NewService(nameof(Without_a_template_seeds_the_default_start_prompt_end_graph));

        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "i", "p", "m"));

        var graph = agent.Draft!.Graph;
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Empty(agent.Draft.FormFields);
    }

    [Fact]
    public async Task With_a_template_seeds_the_templates_graph_instead_of_the_default()
    {
        var service = NewService(nameof(With_a_template_seeds_the_templates_graph_instead_of_the_default));
        var template = AgentTemplateCatalog.Get("rag-qa")!;

        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "", "p", "m"), template: template);

        var graph = agent.Draft!.Graph;
        Assert.Equal(template.Graph.Nodes.Count, graph.Nodes.Count);
        Assert.Contains(graph.Nodes, n => n.Type == "documentSearch");
    }

    [Fact]
    public async Task With_a_template_seeds_its_form_fields()
    {
        var service = NewService(nameof(With_a_template_seeds_its_form_fields));
        var template = AgentTemplateCatalog.Get("validated-form")!;

        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "", "p", "m"), template: template);

        Assert.Equal(2, agent.Draft!.FormFields.Count);
        Assert.Contains(agent.Draft.FormFields, f => f.Name == "email" && f.Required);
    }

    [Fact]
    public async Task Template_system_instructions_fill_in_only_when_request_left_it_blank()
    {
        var service = NewService(nameof(Template_system_instructions_fill_in_only_when_request_left_it_blank));
        var template = AgentTemplateCatalog.Get("rag-qa")!;

        var (blank, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "", "p", "m"), template: template);
        var (withOwn, _) = await service.CreateAsync(new CreateAgentRequest("b", "d", "my own instructions", "p", "m"), template: template);

        Assert.Equal(template.SystemInstructions, blank.SystemInstructions);
        Assert.Equal("my own instructions", withOwn.SystemInstructions);
    }
}
