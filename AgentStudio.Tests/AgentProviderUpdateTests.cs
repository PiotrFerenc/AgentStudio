using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AgentService.UpdateProviderAsync — changing an agent's default provider/model after
/// creation (previously read-only once set).</summary>
public class AgentProviderUpdateTests
{
    private static AgentService NewService(string dbName) =>
        new(new AgentRepository(new AgentStudioDbContext(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options)),
            new ApiKeyService(),
            new UserRepository(Options.Create(new List<UserConfig>())));

    [Fact]
    public async Task UpdateProviderAsync_changes_provider_and_model()
    {
        var service = NewService(nameof(UpdateProviderAsync_changes_provider_and_model));
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "i", "old-provider", "old-model"));

        await service.UpdateProviderAsync(agent.Id, "new-provider", "new-model");

        Assert.Equal("new-provider", agent.ModelProviderName);
        Assert.Equal("new-model", agent.ModelName);
    }

    [Fact]
    public async Task UpdateProviderAsync_empty_provider_throws()
    {
        var service = NewService(nameof(UpdateProviderAsync_empty_provider_throws));
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "i", "p", "m"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProviderAsync(agent.Id, "", "model"));
    }

    [Fact]
    public async Task UpdateProviderAsync_empty_model_throws()
    {
        var service = NewService(nameof(UpdateProviderAsync_empty_model_throws));
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("a", "d", "i", "p", "m"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProviderAsync(agent.Id, "provider", ""));
    }

    [Fact]
    public async Task UpdateProviderAsync_missing_agent_throws()
    {
        var service = NewService(nameof(UpdateProviderAsync_missing_agent_throws));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateProviderAsync(Guid.NewGuid(), "provider", "model"));
    }
}
