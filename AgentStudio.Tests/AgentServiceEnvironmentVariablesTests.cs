using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class AgentServiceEnvironmentVariablesTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task<(AgentService Service, Agent Agent)> Fixture(string dbName)
    {
        var db = NewDb(dbName);
        var repo = new AgentRepository(db);
        var service = new AgentService(repo, new ApiKeyService());
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("t", "", "", "p", "m"));
        return (service, agent);
    }

    [Fact]
    public async Task UpdateEnvironmentVariablesAsync_persists_the_full_set()
    {
        var (service, agent) = await Fixture(nameof(UpdateEnvironmentVariablesAsync_persists_the_full_set));

        await service.UpdateEnvironmentVariablesAsync(agent.Id, new Dictionary<string, string> { ["API_URL"] = "https://a", ["TENANT"] = "acme" });

        var reloaded = await NewDb(nameof(UpdateEnvironmentVariablesAsync_persists_the_full_set)).Agents.FirstAsync(a => a.Id == agent.Id);
        Assert.Equal("https://a", reloaded.EnvironmentVariables["API_URL"]);
        Assert.Equal("acme", reloaded.EnvironmentVariables["TENANT"]);
    }

    [Fact]
    public async Task UpdateEnvironmentVariablesAsync_replaces_wholesale_not_merges()
    {
        var (service, agent) = await Fixture(nameof(UpdateEnvironmentVariablesAsync_replaces_wholesale_not_merges));
        await service.UpdateEnvironmentVariablesAsync(agent.Id, new Dictionary<string, string> { ["OLD"] = "x" });

        await service.UpdateEnvironmentVariablesAsync(agent.Id, new Dictionary<string, string> { ["NEW"] = "y" });

        var reloaded = await NewDb(nameof(UpdateEnvironmentVariablesAsync_replaces_wholesale_not_merges)).Agents.FirstAsync(a => a.Id == agent.Id);
        Assert.False(reloaded.EnvironmentVariables.ContainsKey("OLD"));
        Assert.Equal("y", reloaded.EnvironmentVariables["NEW"]);
    }

    [Fact]
    public async Task UpdateEnvironmentVariablesAsync_missing_agent_fails_clearly()
    {
        var db = NewDb(nameof(UpdateEnvironmentVariablesAsync_missing_agent_fails_clearly));
        var service = new AgentService(new AgentRepository(db), new ApiKeyService());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateEnvironmentVariablesAsync(Guid.NewGuid(), new Dictionary<string, string>()));
    }
}
