using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentStudio.Infrastructure;

/// <summary>Config for the optional conversation-history purge job. Disabled by default —
/// persistent conversation memory (phase 2) is documented as having no TTL by design; this
/// only bounds growth for operators who opt in via the "ConversationRetention" config section.</summary>
public sealed class ConversationRetentionOptions
{
    public bool Enabled { get; set; }
    public int RetentionDays { get; set; } = 90;
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Periodically deletes ConversationState rows whose LastActivityAt is older than
/// RetentionDays. Closes the "Conversations table grows unbounded, no purge job" gap noted in
/// DEPLOYMENT.md, without changing the default no-TTL behavior for operators who don't opt in.</summary>
public sealed class ConversationRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationRetentionOptions _options;
    private readonly ILogger<ConversationRetentionService> _logger;

    public ConversationRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptions<ConversationRetentionOptions> options,
        ILogger<ConversationRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Conversation retention purge failed.");
            }

            try
            {
                await Task.Delay(_options.CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public (not just called from the timed loop) so it can be exercised directly in
    /// tests without waiting on the loop's delay.</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);

        var stale = await db.Conversations.Where(c => c.LastActivityAt < cutoff).ToListAsync(ct);
        if (stale.Count == 0) return;

        db.Conversations.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Conversation retention: purged {Count} conversation(s) older than {Days} day(s).",
            stale.Count, _options.RetentionDays);
    }
}
