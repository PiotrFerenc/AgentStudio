using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentStudio.Infrastructure;

/// <summary>Config for the scheduled-trigger poll loop (phase 11). Unlike
/// <see cref="ConversationRetentionOptions"/> this defaults to enabled — without it, no
/// per-agent schedule (<c>Agent.ScheduleEnabled</c>) would ever fire, defeating the point of the
/// feature; the per-agent flag is the real on/off switch operators actually use.</summary>
public sealed class ScheduledRunnerOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Fires every agent whose <c>ScheduleEnabled</c> is set and whose interval has
/// elapsed since <c>LastScheduledRunAt</c> — a fixed-interval poll, not a real cron expression
/// parser (no new dependency, and "every N minutes" covers the actual use case: automatic
/// periodic runs, not calendar-specific timing). Runs agents one at a time within a single poll
/// pass, so a slow scheduled run can never overlap a later poll tick starting the same agent
/// again — the tradeoff is poll-interval drift if many agents are due at once.
///
/// ponytail: single-process sequential polling, no distributed lock — fine for one instance;
/// running multiple AgentStudio.Web instances against the same DB would double-fire schedules.
/// Upgrade to a DB-level claim (e.g. UPDATE ... WHERE LastScheduledRunAt = @expected) if that
/// ever matters.</summary>
public sealed class ScheduledRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScheduledRunnerOptions _options;
    private readonly ILogger<ScheduledRunner> _logger;

    public ScheduledRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<ScheduledRunnerOptions> options,
        ILogger<ScheduledRunner> logger)
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
                await RunDueSchedulesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Scheduled-run poll pass failed.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public (not just called from the timed loop) so it can be exercised directly in
    /// tests without waiting on the loop's delay.</summary>
    public async Task RunDueSchedulesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var providers = scope.ServiceProvider.GetRequiredService<IProviderRepository>();
        var runner = scope.ServiceProvider.GetRequiredService<WorkflowRunner>();

        var now = DateTimeOffset.UtcNow;
        var due = (await agents.ListAsync(ct))
            .Where(a => a.ScheduleEnabled && a.ScheduleIntervalMinutes is > 0)
            .Where(a => a.LastScheduledRunAt is null || now - a.LastScheduledRunAt >= TimeSpan.FromMinutes(a.ScheduleIntervalMinutes!.Value))
            .ToList();

        foreach (var agent in due)
        {
            try
            {
                await RunOneAsync(agent, providers, runner, ct);
            }
            catch (Exception ex)
            {
                // Infrastructure failure (e.g. provider missing) before/around the run itself —
                // WorkflowRunner never throws for a node-level failure (it yields "[error] ..."
                // text instead), so reaching here means something outside the graph broke.
                // LastScheduledRunAt deliberately isn't touched: the next poll retries promptly
                // instead of waiting a full interval on a config problem someone might just fix.
                _logger.LogError(ex, "Scheduled run failed for agent {AgentId} ({AgentName}).", agent.Id, agent.Name);
            }
        }

        if (due.Count > 0)
            await agents.SaveChangesAsync(ct);
    }

    private static async Task RunOneAsync(Agent agent, IProviderRepository providers, WorkflowRunner runner, CancellationToken ct)
    {
        var version = agent.Versions
            .Where(v => v.Status == AgentVersionStatus.Published)
            .OrderByDescending(v => v.Version)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Agent '{agent.Name}' has no published version.");
        var provider = await providers.GetByNameAsync(agent.ModelProviderName, ct)
            ?? throw new InvalidOperationException($"Agent '{agent.Name}': provider '{agent.ModelProviderName}' is not configured.");

        // Fresh, throwaway conversation per firing — a schedule trigger fires independent runs,
        // not turns in one ongoing conversation. Never persisted via IConversationStore (same
        // choice as a form run); the ExecutionLog is written regardless, which is what actually
        // makes a scheduled run visible on the agent's page — the "schedule-" prefix identifies
        // it there the same way RunForm's "form-" prefix identifies a form submission.
        var conversation = new ConversationState
        {
            ConversationId = $"schedule-{Guid.NewGuid():N}",
            AgentId = agent.Id,
            AgentVersion = version.Version
        };

        await foreach (var _ in runner.RunAsync(agent, version, provider, conversation, agent.ScheduleInput, ct)) { }

        agent.LastScheduledRunAt = DateTimeOffset.UtcNow;
    }
}
