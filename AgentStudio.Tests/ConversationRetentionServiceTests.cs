using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class ConversationRetentionServiceTests
{
    private static ServiceProvider BuildProvider(string dbName) =>
        new ServiceCollection()
            .AddDbContext<AgentStudioDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();

    private static ConversationState Conversation(string id, DateTimeOffset lastActivity) => new()
    {
        ConversationId = id,
        AgentId = Guid.NewGuid(),
        AgentVersion = 1,
        LastActivityAt = lastActivity
    };

    [Fact]
    public async Task PurgeAsync_deletes_stale_conversations_and_keeps_recent_ones()
    {
        await using var provider = BuildProvider(nameof(PurgeAsync_deletes_stale_conversations_and_keeps_recent_ones));
        var now = DateTimeOffset.UtcNow;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
            db.Conversations.Add(Conversation("stale", now.AddDays(-91)));
            db.Conversations.Add(Conversation("fresh", now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var options = Options.Create(new ConversationRetentionOptions { Enabled = true, RetentionDays = 90 });
        var service = new ConversationRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ConversationRetentionService>.Instance);

        await service.PurgeAsync(CancellationToken.None);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
        var remaining = await verifyDb.Conversations.Select(c => c.ConversationId).ToListAsync();
        Assert.Equal(new[] { "fresh" }, remaining);
    }

    [Fact]
    public async Task PurgeAsync_does_nothing_when_no_conversation_is_stale()
    {
        await using var provider = BuildProvider(nameof(PurgeAsync_does_nothing_when_no_conversation_is_stale));
        var now = DateTimeOffset.UtcNow;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
            db.Conversations.Add(Conversation("fresh", now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var options = Options.Create(new ConversationRetentionOptions { Enabled = true, RetentionDays = 90 });
        var service = new ConversationRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ConversationRetentionService>.Instance);

        await service.PurgeAsync(CancellationToken.None);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
        Assert.Equal(1, await verifyDb.Conversations.CountAsync());
    }
}
