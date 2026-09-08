using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class AgentLifecycleTests
{
    private static DbContextOptions<AgentStudioDbContext> Options(string name) =>
        new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(name).Options;

    private static async Task<Guid> CreatePublishedAgentAsync(DbContextOptions<AgentStudioDbContext> options, string? rawKey = null)
    {
        using var db = new AgentStudioDbContext(options);
        var service = new AgentService(new AgentRepository(db), new ApiKeyService());
        var (agent, _) = await service.CreateAsync(new Contracts.CreateAgentRequest("a", "d", "i", "p", "m"));
        var dto = new Contracts.WorkflowGraphDto();
        dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "s", Type = "start" });
        dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "e", Type = "end" });
        dto.Edges.Add(new Contracts.WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
        await service.UpdateDraftAsync(agent.Id, dto);
        await service.PublishAsync(agent.Id);
        return agent.Id;
    }

    [Fact]
    public async Task RegenerateApiKey_invalidates_old_key_and_new_key_verifies()
    {
        var options = Options("regen-test");
        Guid agentId;
        string oldKey;
        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            var (agent, raw) = await service.CreateAsync(new Contracts.CreateAgentRequest("a", "d", "i", "p", "m"));
            agentId = agent.Id;
            oldKey = raw;
        }

        string newKey;
        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            newKey = await service.RegenerateApiKeyAsync(agentId);
            Assert.StartsWith("ask_", newKey);
            Assert.NotEqual(oldKey, newKey);
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            var agent = await new AgentRepository(db).GetAsync(agentId);
            Assert.False(service.VerifyApiKey(agent!, oldKey));
            Assert.True(service.VerifyApiKey(agent!, newKey));
        }
    }

    [Fact]
    public async Task RegenerateApiKey_missing_agent_throws()
    {
        var options = Options("regen-missing-test");
        using var db = new AgentStudioDbContext(options);
        var service = new AgentService(new AgentRepository(db), new ApiKeyService());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RegenerateApiKeyAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Unpublish_sets_status_to_unpublished()
    {
        var options = Options("unpublish-test");
        var agentId = await CreatePublishedAgentAsync(options);

        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            var v = await service.UnpublishAsync(agentId, 1);
            Assert.Equal(AgentVersionStatus.Unpublished, v.Status);
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var agent = await new AgentRepository(db).GetAsync(agentId);
            var v = agent!.Versions.Single(x => x.Version == 1);
            Assert.Equal(AgentVersionStatus.Unpublished, v.Status);
        }
    }

    [Fact]
    public async Task Unpublish_draft_version_not_allowed()
    {
        var options = Options("unpublish-draft-test");
        var agentId = await CreatePublishedAgentAsync(options);

        using var db = new AgentStudioDbContext(options);
        var service = new AgentService(new AgentRepository(db), new ApiKeyService());
        // version 0 is the draft
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnpublishAsync(agentId, 0));
    }

    [Fact]
    public async Task Republish_creates_new_published_version_with_copied_graph()
    {
        var options = Options("republish-test");
        var agentId = await CreatePublishedAgentAsync(options);

        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            // publish a second version so max becomes 2
            var dto = new Contracts.WorkflowGraphDto();
            dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "s", Type = "start" });
            dto.Nodes.Add(new Contracts.WorkflowNodeDto { Id = "e", Type = "end" });
            dto.Edges.Add(new Contracts.WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
            await service.UpdateDraftAsync(agentId, dto);
            await service.PublishAsync(agentId);
        }

        AgentVersion republished;
        using (var db = new AgentStudioDbContext(options))
        {
            var service = new AgentService(new AgentRepository(db), new ApiKeyService());
            republished = await service.RepublishAsync(agentId, 1);
            Assert.Equal(3, republished.Version);
            Assert.Equal(AgentVersionStatus.Published, republished.Status);
            Assert.NotNull(republished.PublishedAt);
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var agent = await new AgentRepository(db).GetAsync(agentId);
            var source = agent!.Versions.Single(x => x.Version == 1);
            var copy = agent.Versions.Single(x => x.Version == 3);
            Assert.Equal(source.GraphJson, copy.GraphJson);
            Assert.NotEqual(source.Id, copy.Id);
        }
    }

    [Fact]
    public async Task Republish_draft_version_not_allowed()
    {
        var options = Options("republish-draft-test");
        var agentId = await CreatePublishedAgentAsync(options);

        using var db = new AgentStudioDbContext(options);
        var service = new AgentService(new AgentRepository(db), new ApiKeyService());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepublishAsync(agentId, 0));
    }
}
