using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class ProviderRepositoryTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static ProviderRepository Repo(AgentStudioDbContext db, params ModelProviderConfig[] configProviders) =>
        new(db, Options.Create(configProviders.ToList()));

    [Fact]
    public async Task ListAsync_includes_both_database_and_config_providers()
    {
        var db = NewDb("providers-both");
        db.Providers.Add(new ModelProviderConfig { Name = "db-one", BaseUrl = "http://db" });
        await db.SaveChangesAsync();

        var repo = Repo(db, new ModelProviderConfig { Name = "config-one", BaseUrl = "http://config" });
        var list = await repo.ListAsync();

        Assert.Contains(list, p => p.Name == "db-one");
        Assert.Contains(list, p => p.Name == "config-one");
    }

    [Fact]
    public async Task ListAsync_config_provider_wins_on_name_collision()
    {
        var db = NewDb("providers-collision");
        db.Providers.Add(new ModelProviderConfig { Name = "shared", BaseUrl = "http://db", DefaultModel = "db-model" });
        await db.SaveChangesAsync();

        var repo = Repo(db, new ModelProviderConfig { Name = "shared", BaseUrl = "http://config", DefaultModel = "config-model" });
        var list = await repo.ListAsync();

        var shared = Assert.Single(list, p => p.Name == "shared");
        Assert.Equal("http://config", shared.BaseUrl);
        Assert.True(shared.IsFromConfig);
    }

    [Fact]
    public async Task GetByNameAsync_prefers_config_over_database()
    {
        var db = NewDb("providers-getbyname");
        db.Providers.Add(new ModelProviderConfig { Name = "shared", BaseUrl = "http://db" });
        await db.SaveChangesAsync();

        var repo = Repo(db, new ModelProviderConfig { Name = "shared", BaseUrl = "http://config" });
        var found = await repo.GetByNameAsync("shared");

        Assert.NotNull(found);
        Assert.Equal("http://config", found!.BaseUrl);
    }

    [Fact]
    public async Task Config_provider_gets_a_stable_deterministic_id()
    {
        var db = NewDb("providers-stable-id");
        var repo1 = Repo(db, new ModelProviderConfig { Name = "stable", BaseUrl = "http://x" });
        var repo2 = Repo(db, new ModelProviderConfig { Name = "stable", BaseUrl = "http://x" });

        var id1 = (await repo1.GetByNameAsync("stable"))!.Id;
        var id2 = (await repo2.GetByNameAsync("stable"))!.Id;

        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Database_only_providers_are_not_marked_as_from_config()
    {
        var db = NewDb("providers-db-only");
        db.Providers.Add(new ModelProviderConfig { Name = "db-only", BaseUrl = "http://db" });
        await db.SaveChangesAsync();

        var repo = Repo(db);
        var found = await repo.GetByNameAsync("db-only");

        Assert.NotNull(found);
        Assert.False(found!.IsFromConfig);
    }
}
