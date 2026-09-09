using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class PublishIntegrationTests
{
    private static AgentStudioDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_update_publish_roundtrip()
    {
        // Request 1: create
        Guid agentId;
        using (var db = NewDb()) { }

        var options = new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase("pub-test").Options;

        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService(), new UserRepository(Options.Create(new List<UserConfig>())));
            var (agent, _) = await service.CreateAsync(new Contracts.CreateAgentRequest("a", "d", "i", "p", "m"));
            agentId = agent.Id;
        }

        // Request 2: update draft
        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService(), new UserRepository(Options.Create(new List<UserConfig>())));
            var dto = new Contracts.WorkflowGraphDto();
            dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "s", Type = "start" });
            dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "e", Type = "end" });
            dto.Edges.Add(new Contracts.WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
            var result = await service.UpdateDraftAsync(agentId, dto);
            Assert.True(result.IsValid);
        }

        // Request 3: publish
        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService(), new UserRepository(Options.Create(new List<UserConfig>())));
            var published = await service.PublishAsync(agentId);
            Assert.Equal(1, published.Version);
            Assert.Equal(AgentVersionStatus.Published, published.Status);
        }
    }
}
