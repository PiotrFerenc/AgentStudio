using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentStudio.Infrastructure;

public sealed class AgentRepository : IAgentRepository
{
    private readonly AgentStudioDbContext _db;
    public AgentRepository(AgentStudioDbContext db) => _db = db;

    public async Task<Agent?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _db.Agents.Include(a => a.Versions).AsTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<List<Agent>> ListAsync(CancellationToken ct = default) =>
        await _db.Agents.Include(a => a.Versions).AsTracking().OrderBy(a => a.Name).ToListAsync(ct);

    public async Task AddAsync(Agent agent, CancellationToken ct = default) => await _db.Agents.AddAsync(agent, ct);
    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

public sealed class ProviderRepository : IProviderRepository
{
    private readonly AgentStudioDbContext _db;
    public ProviderRepository(AgentStudioDbContext db) => _db = db;

    public async Task<ModelProviderConfig?> GetByNameAsync(string name, CancellationToken ct = default) =>
        await _db.Providers.FirstOrDefaultAsync(p => p.Name == name, ct);

    public async Task<List<ModelProviderConfig>> ListAsync(CancellationToken ct = default) =>
        await _db.Providers.OrderBy(p => p.Name).ToListAsync(ct);

    public async Task AddAsync(ModelProviderConfig provider, CancellationToken ct = default) => await _db.Providers.AddAsync(provider, ct);
    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}

public sealed class PendingApprovalRepository : IPendingApprovalRepository
{
    private readonly AgentStudioDbContext _db;
    public PendingApprovalRepository(AgentStudioDbContext db) => _db = db;

    public async Task<List<PendingApproval>> ListPendingAsync(CancellationToken ct = default) =>
        await _db.PendingApprovals.Where(a => a.Status == ApprovalStatus.Pending).OrderBy(a => a.CreatedAt).ToListAsync(ct);

    public async Task<PendingApproval?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _db.PendingApprovals.FindAsync(new object[] { id }, ct);

    public async Task AddAsync(PendingApproval approval, CancellationToken ct = default) => await _db.PendingApprovals.AddAsync(approval, ct);
    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
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

public sealed class UserRepository : IUserRepository
{
    private readonly AgentStudioDbContext _db;
    public UserRepository(AgentStudioDbContext db) => _db = db;

    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default) =>
        _db.Users.OrderBy(u => u.Username).ToListAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) => _db.Users.AnyAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct = default) => await _db.Users.AddAsync(user, ct);

    public Task DeleteAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Remove(user);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
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

        var byDay = logs
            .GroupBy(l => DateOnly.FromDateTime(l.StartedAt.UtcDateTime.Date))
            .Select(g => new DailyCount(g.Key, g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

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
