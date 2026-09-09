using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class GraphComponentRepositoryTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task Add_then_list_returns_it_ordered_by_name()
    {
        var db = NewDb("components-add-list");
        var repo = new GraphComponentRepository(db);

        await repo.AddAsync(new GraphComponent { Name = "Zeta", GraphJson = "{}" });
        await repo.AddAsync(new GraphComponent { Name = "Alpha", GraphJson = "{}" });
        await repo.SaveChangesAsync();

        var all = await repo.ListAsync();
        Assert.Equal(new[] { "Alpha", "Zeta" }, all.Select(c => c.Name));
    }

    [Fact]
    public async Task Get_returns_the_stored_graph_json()
    {
        var db = NewDb("components-get");
        var repo = new GraphComponentRepository(db);
        var component = new GraphComponent { Name = "Snippet", GraphJson = """{"nodes":[],"edges":[]}""" };
        await repo.AddAsync(component);
        await repo.SaveChangesAsync();

        var fetched = await repo.GetAsync(component.Id);

        Assert.NotNull(fetched);
        Assert.Equal("""{"nodes":[],"edges":[]}""", fetched!.GraphJson);
    }

    [Fact]
    public async Task Delete_removes_it()
    {
        var db = NewDb("components-delete");
        var repo = new GraphComponentRepository(db);
        var component = new GraphComponent { Name = "Temp", GraphJson = "{}" };
        await repo.AddAsync(component);
        await repo.SaveChangesAsync();

        await repo.DeleteAsync(component);
        await repo.SaveChangesAsync();

        Assert.Null(await repo.GetAsync(component.Id));
    }

    [Fact]
    public async Task Get_missing_id_returns_null()
    {
        var db = NewDb("components-missing");
        var repo = new GraphComponentRepository(db);

        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
    }
}
