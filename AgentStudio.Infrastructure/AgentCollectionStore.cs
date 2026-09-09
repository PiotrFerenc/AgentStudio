using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentStudio.Infrastructure;

public sealed class EfAgentCollectionStore : IAgentCollectionStore
{
    private readonly AgentStudioDbContext _db;

    public EfAgentCollectionStore(AgentStudioDbContext db) => _db = db;

    public async Task<string?> GetAsync(Guid agentId, string key, CancellationToken ct = default)
    {
        var entry = await _db.AgentCollectionEntries.FindAsync(new object[] { agentId, key }, ct);
        return entry?.Value;
    }

    public async Task SetAsync(Guid agentId, string key, string value, CancellationToken ct = default)
    {
        var entry = await _db.AgentCollectionEntries.FindAsync(new object[] { agentId, key }, ct);
        if (entry is null)
        {
            _db.AgentCollectionEntries.Add(new AgentCollectionEntry { AgentId = agentId, Key = key, Value = value });
        }
        else
        {
            entry.Value = value;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<AgentCollectionEntry>> ListAsync(Guid agentId, CancellationToken ct = default) =>
        await _db.AgentCollectionEntries.Where(e => e.AgentId == agentId).OrderBy(e => e.Key).ToListAsync(ct);

    public async Task DeleteAsync(Guid agentId, string key, CancellationToken ct = default)
    {
        var entry = await _db.AgentCollectionEntries.FindAsync(new object[] { agentId, key }, ct);
        if (entry is not null)
        {
            _db.AgentCollectionEntries.Remove(entry);
            await _db.SaveChangesAsync(ct);
        }
    }
}
