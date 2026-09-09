using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class ProviderRepositoryTests
{
    private static ProviderRepository Repo(params ModelProviderConfig[] providers) =>
        new(Options.Create(providers.ToList()));

    [Fact]
    public async Task ListAsync_returns_all_configured_providers()
    {
        var repo = Repo(
            new ModelProviderConfig { Name = "one", BaseUrl = "http://x" },
            new ModelProviderConfig { Name = "two", BaseUrl = "http://y" });

        var list = await repo.ListAsync();

        Assert.Contains(list, p => p.Name == "one");
        Assert.Contains(list, p => p.Name == "two");
    }

    [Fact]
    public async Task GetByNameAsync_returns_the_matching_provider()
    {
        var repo = Repo(new ModelProviderConfig { Name = "shared", BaseUrl = "http://config" });

        var found = await repo.GetByNameAsync("shared");

        Assert.NotNull(found);
        Assert.Equal("http://config", found!.BaseUrl);
    }

    [Fact]
    public async Task GetByNameAsync_unknown_name_returns_null()
    {
        var repo = Repo(new ModelProviderConfig { Name = "known", BaseUrl = "http://x" });

        Assert.Null(await repo.GetByNameAsync("unknown"));
    }

    [Fact]
    public async Task Provider_gets_a_stable_deterministic_id_across_repository_instances()
    {
        // Two separate config-bound instances with the same name — simulates two process
        // restarts each freshly binding "ModelProviders" from appsettings.json.
        var id1 = (await Repo(new ModelProviderConfig { Name = "stable", BaseUrl = "http://x" }).GetByNameAsync("stable"))!.Id;
        var id2 = (await Repo(new ModelProviderConfig { Name = "stable", BaseUrl = "http://x" }).GetByNameAsync("stable"))!.Id;

        Assert.Equal(id1, id2);
    }
}
