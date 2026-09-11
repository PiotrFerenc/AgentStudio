using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentStudio.Infrastructure;

/// <summary>
/// Design-time factory used by `dotnet ef` so migrations are scaffolded against
/// the Sqlite provider regardless of the runtime ConnectionStrings:AgentStudio value
/// (which is "InMemory" in development). The connection string here is a placeholder —
/// it is only used for model snapshot generation, never to connect.
/// </summary>
public sealed class AgentStudioDbContextFactory : IDesignTimeDbContextFactory<AgentStudioDbContext>
{
    public AgentStudioDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentStudioDbContext>()
            .UseSqlite("Data Source=agentstudio.db")
            .Options;
        return new AgentStudioDbContext(options);
    }
}
