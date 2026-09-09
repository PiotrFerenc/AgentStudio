using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class PendingApprovalRepositoryTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task ListPendingAsync_returns_only_pending_ones()
    {
        var db = NewDb(nameof(ListPendingAsync_returns_only_pending_ones));
        var repo = new PendingApprovalRepository(db);
        var pending = new PendingApproval { AgentId = Guid.NewGuid(), AgentVersion = 1, NodeId = "a", Status = ApprovalStatus.Pending };
        var decided = new PendingApproval { AgentId = Guid.NewGuid(), AgentVersion = 1, NodeId = "a", Status = ApprovalStatus.Approved };
        await repo.AddAsync(pending);
        await repo.AddAsync(decided);
        await repo.SaveChangesAsync();

        var list = await repo.ListPendingAsync();

        Assert.Single(list);
        Assert.Equal(pending.Id, list[0].Id);
    }

    [Fact]
    public async Task Get_round_trips_the_variables_snapshot()
    {
        var db = NewDb(nameof(Get_round_trips_the_variables_snapshot));
        var repo = new PendingApprovalRepository(db);
        var approval = new PendingApproval
        {
            AgentId = Guid.NewGuid(),
            AgentVersion = 2,
            NodeId = "a",
            Message = "approve?",
            Variables = new Dictionary<string, string> { ["amount"] = "100" }
        };
        await repo.AddAsync(approval);
        await repo.SaveChangesAsync();

        var fetched = await repo.GetAsync(approval.Id);

        Assert.NotNull(fetched);
        Assert.Equal("100", fetched!.Variables["amount"]);
        Assert.Equal("approve?", fetched.Message);
    }

    [Fact]
    public async Task Get_missing_id_returns_null()
    {
        var db = NewDb(nameof(Get_missing_id_returns_null));
        var repo = new PendingApprovalRepository(db);

        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
    }
}
