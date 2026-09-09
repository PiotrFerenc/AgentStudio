using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentStudio.Infrastructure;

public sealed class AgentRepository : IAgentRepository
{
    private readonly AgentStudioDbContext _db;
    public AgentRepository(AgentStudioDbContext db) => _db = db;

    public async Task<Agent?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _db.Agents.Include(a => a.Versions).Include(a => a.Collaborators).AsTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<List<Agent>> ListAsync(CancellationToken ct = default) =>
        await _db.Agents.Include(a => a.Versions).Include(a => a.Collaborators).AsTracking().OrderBy(a => a.Name).ToListAsync(ct);

    public async Task AddAsync(Agent agent, CancellationToken ct = default) => await _db.Agents.AddAsync(agent, ct);
    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

/// <summary>Providers are entirely appsettings.json's "ModelProviders" array — no database table
/// (dropped, see the RemoveProvidersAndUsersTables migration). Each gets a deterministic Id
/// (name-derived — see DeterministicGuid) since appsettings has nowhere to persist a random
/// one.</summary>
public sealed class ProviderRepository : IProviderRepository
{
    private readonly Microsoft.Extensions.Options.IOptions<List<ModelProviderConfig>> _configProviders;

    public ProviderRepository(Microsoft.Extensions.Options.IOptions<List<ModelProviderConfig>> configProviders) =>
        _configProviders = configProviders;

    private List<ModelProviderConfig> ConfigProviders()
    {
        foreach (var p in _configProviders.Value)
            p.Id = DeterministicGuid.From($"provider:{p.Name}");
        return _configProviders.Value;
    }

    public Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(ConfigProviders().FirstOrDefault(p => p.Name == name));

    public Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(ConfigProviders().OrderBy(p => p.Name).ToList());
}

public sealed class GraphComponentRepository : IGraphComponentRepository
{
    private readonly AgentStudioDbContext _db;
    public GraphComponentRepository(AgentStudioDbContext db) => _db = db;

    public async Task<List<GraphComponent>> ListAsync(CancellationToken ct = default) =>
        await _db.GraphComponents.OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<GraphComponent?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _db.GraphComponents.FindAsync(new object[] { id }, ct);

    public async Task AddAsync(GraphComponent component, CancellationToken ct = default) => await _db.GraphComponents.AddAsync(component, ct);
    public Task DeleteAsync(GraphComponent component, CancellationToken ct = default) { _db.GraphComponents.Remove(component); return Task.CompletedTask; }
    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

/// <summary>Users are entirely appsettings.json's "Users" array — no database table (dropped,
/// see the RemoveProvidersAndUsersTables migration). Each gets a deterministic Id
/// (DeterministicGuid) since appsettings has nowhere to persist a random one —
/// Agent.OwnerId/AgentCollaborator.UserId reference it, so it has to stay stable across
/// restarts for ownership to keep working.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly Microsoft.Extensions.Options.IOptions<List<UserConfig>> _configUsers;

    public UserRepository(Microsoft.Extensions.Options.IOptions<List<UserConfig>> configUsers) =>
        _configUsers = configUsers;

    private List<User> ConfigUsers() => _configUsers.Value.Select(c => new User
    {
        Id = DeterministicGuid.From($"user:{c.Username}"),
        Username = c.Username,
        Role = c.Role,
        PasswordHash = c.Password is null ? c.PasswordHash ?? "" : "",
        ConfigPlaintextPassword = c.Password
    }).ToList();

    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(ConfigUsers().FirstOrDefault(u => u.Id == id));

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(ConfigUsers().FirstOrDefault(u => u.Username == username));

    public Task<List<User>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(ConfigUsers().OrderBy(u => u.Username).ToList());

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(_configUsers.Value.Count > 0);
}

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly AgentStudioDbContext _db;
    public DocumentRepository(AgentStudioDbContext db) => _db = db;

    public Task<List<Document>> ListAsync(Guid agentId, CancellationToken ct = default) =>
        _db.Documents.Where(d => d.AgentId == agentId).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);

    public Task<Document?> GetAsync(Guid documentId, CancellationToken ct = default) =>
        _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);

    public Task<int> CountChunksAsync(Guid documentId, CancellationToken ct = default) =>
        _db.DocumentChunks.CountAsync(c => c.DocumentId == documentId, ct);

    public Task<List<DocumentChunk>> ListChunksAsync(Guid agentId, CancellationToken ct = default) =>
        _db.DocumentChunks.Where(c => c.AgentId == agentId).ToListAsync(ct);

    public async Task AddAsync(Document document, CancellationToken ct = default) => await _db.Documents.AddAsync(document, ct);

    public async Task AddChunksAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default) => await _db.DocumentChunks.AddRangeAsync(chunks, ct);

    public Task DeleteAsync(Document document, CancellationToken ct = default)
    {
        _db.Documents.Remove(document);
        return Task.CompletedTask;
    }

    public async Task DeleteChunksAsync(Guid documentId, CancellationToken ct = default)
    {
        var chunks = await _db.DocumentChunks.Where(c => c.DocumentId == documentId).ToListAsync(ct);
        _db.DocumentChunks.RemoveRange(chunks);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

public sealed class ExecutionLogRepository : IExecutionLogRepository
{
    private readonly AgentStudioDbContext _db;
    public ExecutionLogRepository(AgentStudioDbContext db) => _db = db;

    public async Task AddAsync(ExecutionLog log, CancellationToken ct = default) => await _db.ExecutionLogs.AddAsync(log, ct);

    public async Task<ExecutionLog?> GetAsync(string executionId, CancellationToken ct = default) =>
        await _db.ExecutionLogs.FirstOrDefaultAsync(l => l.ExecutionId == executionId, ct);

    public async Task<List<ExecutionLog>> ListForAgentAsync(Guid agentId, int take = 50, CancellationToken ct = default) =>
        await _db.ExecutionLogs.Where(l => l.AgentId == agentId).OrderByDescending(l => l.StartedAt).Take(take).ToListAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

public sealed class AnalyticsRepository : IAnalyticsRepository
{
    private readonly AgentStudioDbContext _db;
    public AnalyticsRepository(AgentStudioDbContext db) => _db = db;

    public async Task<AnalyticsSummary> GetSummaryAsync(int days = 30, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var logs = await _db.ExecutionLogs
            .Where(l => l.StartedAt >= since)
            .Select(l => new { l.AgentId, l.StartedAt, l.CompletedAt, l.Status, l.ConversationId })
            .ToListAsync(ct);

        var agentNames = await _db.Agents.Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        static double AvgSeconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset? End)> rows)
        {
            var durations = rows.Where(r => r.End.HasValue).Select(r => (r.End!.Value - r.Start).TotalSeconds).ToList();
            return durations.Count == 0 ? 0 : durations.Average();
        }

        var byAgent = logs
            .GroupBy(l => l.AgentId)
            .Select(g => new AgentUsage(
                g.Key,
                agentNames.TryGetValue(g.Key, out var name) ? name : "(deleted agent)",
                g.Count(),
                g.Count(l => l.Status == "failed"),
                AvgSeconds(g.Select(l => (l.StartedAt, l.CompletedAt)))))
            .OrderByDescending(a => a.Executions)
            .ToList();

        // Zero-filled for every day in the window, not just days that had an execution — a
        // sparse list (skipping empty days entirely) would make the "Executions per day" chart
        // silently compress a gap (e.g. a weekend with no runs) into adjacent bars, making two
        // days that are actually far apart on the calendar look consecutive.
        var byDayLookup = logs
            .GroupBy(l => DateOnly.FromDateTime(l.StartedAt.UtcDateTime.Date))
            .ToDictionary(g => g.Key, g => g.Count());
        var byDay = new List<DailyCount>();
        for (var d = DateOnly.FromDateTime(since.UtcDateTime.Date); d <= DateOnly.FromDateTime(DateTime.UtcNow.Date); d = d.AddDays(1))
            byDay.Add(new DailyCount(d, byDayLookup.GetValueOrDefault(d, 0)));

        // "form-" prefix is the same throwaway-conversation convention SubAgentNode uses with
        // "subagent-" — see WorkflowRunner.cs. Reuses the logs list already fetched above, no
        // extra query.
        var formLogs = logs.Where(l => l.ConversationId.StartsWith("form-", StringComparison.Ordinal)).ToList();

        return new AnalyticsSummary(
            TotalExecutions: logs.Count,
            FailedExecutions: logs.Count(l => l.Status == "failed"),
            ActiveAgents: byAgent.Count,
            AvgDurationSeconds: AvgSeconds(logs.Select(l => (l.StartedAt, l.CompletedAt))),
            ByAgent: byAgent,
            ByDay: byDay,
            FormSubmissions: formLogs.Count,
            FormFailedSubmissions: formLogs.Count(l => l.Status == "failed"));
    }
}
