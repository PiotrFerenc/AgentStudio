using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class AgentServiceScheduleTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task<(AgentService Service, Agent Agent)> Fixture(string dbName)
    {
        var db = NewDb(dbName);
        var repo = new AgentRepository(db);
        var service = new AgentService(repo, new ApiKeyService(), new UserRepository(Options.Create(new List<UserConfig>())));
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("t", "", "", "p", "m"));
        return (service, agent);
    }

    [Fact]
    public async Task UpdateScheduleAsync_persists_all_fields()
    {
        var (service, agent) = await Fixture(nameof(UpdateScheduleAsync_persists_all_fields));

        await service.UpdateScheduleAsync(agent.Id, enabled: true, intervalMinutes: 15, input: "hello");

        var db = NewDb(nameof(UpdateScheduleAsync_persists_all_fields));
        var reloaded = await db.Agents.FirstAsync(a => a.Id == agent.Id);
        Assert.True(reloaded.ScheduleEnabled);
        Assert.Equal(15, reloaded.ScheduleIntervalMinutes);
        Assert.Equal("hello", reloaded.ScheduleInput);
    }

    [Fact]
    public async Task UpdateScheduleAsync_enabling_without_an_interval_fails_clearly()
    {
        var (service, agent) = await Fixture(nameof(UpdateScheduleAsync_enabling_without_an_interval_fails_clearly));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateScheduleAsync(agent.Id, enabled: true, intervalMinutes: null, input: ""));
        Assert.Contains("interval", ex.Message);
    }

    [Fact]
    public async Task UpdateScheduleAsync_zero_or_negative_interval_fails_clearly()
    {
        var (service, agent) = await Fixture(nameof(UpdateScheduleAsync_zero_or_negative_interval_fails_clearly));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateScheduleAsync(agent.Id, enabled: true, intervalMinutes: 0, input: ""));
    }

    [Fact]
    public async Task UpdateScheduleAsync_disabling_does_not_require_an_interval()
    {
        var (service, agent) = await Fixture(nameof(UpdateScheduleAsync_disabling_does_not_require_an_interval));

        await service.UpdateScheduleAsync(agent.Id, enabled: false, intervalMinutes: null, input: "");

        var db = NewDb(nameof(UpdateScheduleAsync_disabling_does_not_require_an_interval));
        var reloaded = await db.Agents.FirstAsync(a => a.Id == agent.Id);
        Assert.False(reloaded.ScheduleEnabled);
    }

    [Fact]
    public async Task UpdateScheduleAsync_missing_agent_fails_clearly()
    {
        var db = NewDb(nameof(UpdateScheduleAsync_missing_agent_fails_clearly));
        var service = new AgentService(new AgentRepository(db), new ApiKeyService(), new UserRepository(Options.Create(new List<UserConfig>())));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateScheduleAsync(Guid.NewGuid(), enabled: false, intervalMinutes: null, input: ""));
    }
}
