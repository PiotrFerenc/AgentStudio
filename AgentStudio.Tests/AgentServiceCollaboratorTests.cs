using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class AgentServiceCollaboratorTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static readonly UserRepository Users = new(Options.Create(new List<UserConfig>
    {
        new() { Username = "owner", Password = "x", Role = UserRole.Editor },
        new() { Username = "carol", Password = "x", Role = UserRole.Editor },
    }));

    private static async Task<(AgentService Service, Agent Agent, AgentStudioDbContext Db)> Fixture(string dbName)
    {
        var db = NewDb(dbName);
        var service = new AgentService(new AgentRepository(db), new ApiKeyService(), Users);
        var owner = (await Users.GetByUsernameAsync("owner"))!;
        var (agent, _) = await service.CreateAsync(new CreateAgentRequest("t", "", "", "p", "m"), owner.Id, owner.Username);
        return (service, agent, db);
    }

    [Fact]
    public async Task CreateAsync_sets_owner()
    {
        var (_, agent, _) = await Fixture(nameof(CreateAsync_sets_owner));

        Assert.Equal("owner", agent.OwnerUsername);
        Assert.NotNull(agent.OwnerId);
    }

    [Fact]
    public async Task AddCollaboratorAsync_grants_access_by_username()
    {
        var (service, agent, db) = await Fixture(nameof(AddCollaboratorAsync_grants_access_by_username));

        await service.AddCollaboratorAsync(agent.Id, "carol");

        var reloaded = await db.Agents.Include(a => a.Collaborators).FirstAsync(a => a.Id == agent.Id);
        Assert.Contains(reloaded.Collaborators, c => c.Username == "carol");
    }

    [Fact]
    public async Task AddCollaboratorAsync_unknown_username_fails_clearly()
    {
        var (service, agent, _) = await Fixture(nameof(AddCollaboratorAsync_unknown_username_fails_clearly));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AddCollaboratorAsync(agent.Id, "nobody"));
        Assert.Contains("nobody", ex.Message);
    }

    [Fact]
    public async Task AddCollaboratorAsync_twice_is_idempotent()
    {
        var (service, agent, db) = await Fixture(nameof(AddCollaboratorAsync_twice_is_idempotent));

        await service.AddCollaboratorAsync(agent.Id, "carol");
        await service.AddCollaboratorAsync(agent.Id, "carol");

        var reloaded = await db.Agents.Include(a => a.Collaborators).FirstAsync(a => a.Id == agent.Id);
        Assert.Single(reloaded.Collaborators);
    }

    [Fact]
    public async Task RemoveCollaboratorAsync_revokes_access()
    {
        var (service, agent, db) = await Fixture(nameof(RemoveCollaboratorAsync_revokes_access));
        await service.AddCollaboratorAsync(agent.Id, "carol");
        var carolId = (await Users.GetByUsernameAsync("carol"))!.Id;

        await service.RemoveCollaboratorAsync(agent.Id, carolId);

        var reloaded = await db.Agents.Include(a => a.Collaborators).FirstAsync(a => a.Id == agent.Id);
        Assert.Empty(reloaded.Collaborators);
    }
}
