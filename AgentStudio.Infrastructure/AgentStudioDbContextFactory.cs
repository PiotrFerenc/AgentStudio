using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentStudio.Infrastructure;

/// <summary>
/// Design-time factory used by `dotnet ef` so migrations are scaffolded against
/// the Npgsql provider regardless of the runtime ConnectionStrings:AgentStudio value
/// (which is "InMemory" in development). The connection string here is a placeholder —
/// it is only used for model snapshot generation, never to connect.
/// </summary>
public sealed class AgentStudioDbContextFactory : IDesignTimeDbContextFactory<AgentStudioDbContext>
{
    public AgentStudioDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentStudioDbContext>()
            .UseNpgsql("Host=localhost;Database=agentstudio;Username=agentstudio;Password=agentstudio")
            .Options;
        return new AgentStudioDbContext(options);
    }
}
