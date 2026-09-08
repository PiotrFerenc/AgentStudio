using AgentStudio.Application;
using AgentStudio.Domain;

namespace AgentStudio.Infrastructure;

/// <summary>
/// EF-backed conversation history (phase 2, etap 2) — replaces the phase-1 in-memory/TTL store.
/// No auto-expiry: a conversation is resumable for as long as its row exists.
/// </summary>
public sealed class PersistentConversationStore : IConversationStore
{
    private readonly AgentStudioDbContext _db;

    public PersistentConversationStore(AgentStudioDbContext db) => _db = db;

    public ConversationState GetOrCreate(string? conversationId, Guid agentId, int agentVersion)
    {
        ConversationState? existing = null;
        if (!string.IsNullOrWhiteSpace(conversationId))
            existing = _db.Conversations.Find(conversationId);

        if (existing is not null && existing.AgentId == agentId && existing.AgentVersion == agentVersion)
            return existing;

        // A provided id that's brand new is honored as-is; a missing id, or one that collides
        // with a conversation owned by a different agent/version, gets a fresh id instead.
        var id = existing is null && !string.IsNullOrWhiteSpace(conversationId)
            ? conversationId!
            : Guid.NewGuid().ToString("N");

        var state = new ConversationState { ConversationId = id, AgentId = agentId, AgentVersion = agentVersion };
        _db.Conversations.Add(state);
        return state;
    }

    public void Save(ConversationState state)
    {
        state.LastActivityAt = DateTimeOffset.UtcNow;
        _db.SaveChanges();
    }
}
