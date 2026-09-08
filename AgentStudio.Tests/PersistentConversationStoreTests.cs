using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class PersistentConversationStoreTests
{
    private static DbContextOptions<AgentStudioDbContext> Options(string name) =>
        new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(name).Options;

    [Fact]
    public void GetOrCreate_without_id_creates_a_new_conversation()
    {
        using var db = new AgentStudioDbContext(Options("conv-new"));
        var store = new PersistentConversationStore(db);
        var agentId = Guid.NewGuid();

        var state = store.GetOrCreate(null, agentId, 1);

        Assert.NotEmpty(state.ConversationId);
        Assert.Equal(agentId, state.AgentId);
        Assert.Equal(1, state.AgentVersion);
    }

    [Fact]
    public void Conversation_survives_across_separate_DbContext_instances()
    {
        var options = Options("conv-persist");
        var agentId = Guid.NewGuid();
        string conversationId;

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var state = store.GetOrCreate(null, agentId, 1);
            state.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            state.Variables["foo"] = "bar";
            store.Save(state);
            conversationId = state.ConversationId;
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var resumed = store.GetOrCreate(conversationId, agentId, 1);

            Assert.Contains(resumed.Messages, m => m.Content == "hello");
            Assert.Equal("bar", resumed.Variables["foo"]);
        }
    }

    [Fact]
    public void GetOrCreate_id_owned_by_a_different_agent_starts_a_fresh_conversation()
    {
        var options = Options("conv-mismatch");
        var agentId = Guid.NewGuid();
        string conversationId;

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var state = store.GetOrCreate(null, agentId, 1);
            store.Save(state);
            conversationId = state.ConversationId;
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var otherAgentId = Guid.NewGuid();

            var fresh = store.GetOrCreate(conversationId, otherAgentId, 1);

            Assert.NotEqual(conversationId, fresh.ConversationId);
            Assert.Equal(otherAgentId, fresh.AgentId);
        }
    }

    [Fact]
    public void No_expiry_conversation_resumes_after_a_long_time()
    {
        var options = Options("conv-no-ttl");
        var agentId = Guid.NewGuid();
        string conversationId;

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var state = store.GetOrCreate(null, agentId, 1);
            store.Save(state);
            state.LastActivityAt = DateTimeOffset.UtcNow.AddYears(-1);
            db.SaveChanges();
            conversationId = state.ConversationId;
        }

        using (var db = new AgentStudioDbContext(options))
        {
            var store = new PersistentConversationStore(db);
            var resumed = store.GetOrCreate(conversationId, agentId, 1);

            Assert.Equal(conversationId, resumed.ConversationId);
        }
    }
}
