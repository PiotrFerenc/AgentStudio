using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class AnalyticsRepositoryTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static ExecutionLog Log(Guid agentId, string status, DateTimeOffset started, TimeSpan? duration, string? conversationId = null) =>
        new()
        {
            ExecutionId = Guid.NewGuid().ToString(),
            ConversationId = conversationId ?? Guid.NewGuid().ToString(),
            AgentId = agentId,
            AgentVersion = 1,
            StartedAt = started,
            CompletedAt = duration is null ? null : started + duration,
            Status = status,
        };

    [Fact]
    public async Task GetSummaryAsync_aggregates_totals_errors_and_duration()
    {
        var db = NewDb("analytics-totals");
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agentA, Name = "Agent A" });
        db.Agents.Add(new Agent { Id = agentB, Name = "Agent B" });
        var now = DateTimeOffset.UtcNow;
        db.ExecutionLogs.Add(Log(agentA, "completed", now.AddHours(-1), TimeSpan.FromSeconds(10)));
        db.ExecutionLogs.Add(Log(agentA, "failed", now.AddHours(-2), TimeSpan.FromSeconds(2)));
        db.ExecutionLogs.Add(Log(agentB, "completed", now.AddHours(-3), TimeSpan.FromSeconds(6)));
        await db.SaveChangesAsync();

        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 30);

        Assert.Equal(3, summary.TotalExecutions);
        Assert.Equal(1, summary.FailedExecutions);
        Assert.Equal(2, summary.ActiveAgents);
        Assert.Equal(6, summary.AvgDurationSeconds, precision: 3); // (10+2+6)/3

        var byAgentA = summary.ByAgent.Single(a => a.AgentId == agentA);
        Assert.Equal(2, byAgentA.Executions);
        Assert.Equal(1, byAgentA.Failed);
        Assert.Equal("Agent A", byAgentA.AgentName);
    }

    [Fact]
    public async Task GetSummaryAsync_excludes_executions_outside_window()
    {
        var db = NewDb("analytics-window");
        var agent = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agent, Name = "Old Agent" });
        var now = DateTimeOffset.UtcNow;
        db.ExecutionLogs.Add(Log(agent, "completed", now.AddDays(-1), TimeSpan.FromSeconds(1)));
        db.ExecutionLogs.Add(Log(agent, "completed", now.AddDays(-40), TimeSpan.FromSeconds(1)));
        await db.SaveChangesAsync();

        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 30);

        Assert.Equal(1, summary.TotalExecutions);
    }

    [Fact]
    public async Task GetSummaryAsync_running_executions_excluded_from_avg_duration()
    {
        var db = NewDb("analytics-running");
        var agent = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agent, Name = "Agent" });
        var now = DateTimeOffset.UtcNow;
        db.ExecutionLogs.Add(Log(agent, "running", now, null));
        db.ExecutionLogs.Add(Log(agent, "completed", now, TimeSpan.FromSeconds(4)));
        await db.SaveChangesAsync();

        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 30);

        Assert.Equal(2, summary.TotalExecutions);
        Assert.Equal(4, summary.AvgDurationSeconds, precision: 3);
    }

    [Fact]
    public async Task GetSummaryAsync_no_data_returns_zeroed_summary()
    {
        var db = NewDb("analytics-empty");
        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 30);

        Assert.Equal(0, summary.TotalExecutions);
        Assert.Equal(0, summary.ActiveAgents);
        Assert.Equal(0, summary.AvgDurationSeconds);
        Assert.Empty(summary.ByAgent);
        Assert.Empty(summary.ByDay);
        Assert.Equal(0, summary.FormSubmissions);
        Assert.Equal(0, summary.FormFailedSubmissions);
    }

    [Fact]
    public async Task GetSummaryAsync_counts_form_submissions_by_conversationId_prefix()
    {
        var db = NewDb("analytics-forms");
        var agent = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agent, Name = "Agent" });
        var now = DateTimeOffset.UtcNow;
        db.ExecutionLogs.Add(Log(agent, "completed", now, TimeSpan.FromSeconds(1), conversationId: $"form-{Guid.NewGuid():N}"));
        db.ExecutionLogs.Add(Log(agent, "completed", now, TimeSpan.FromSeconds(1), conversationId: $"form-{Guid.NewGuid():N}"));
        db.ExecutionLogs.Add(Log(agent, "failed", now, TimeSpan.FromSeconds(1), conversationId: $"form-{Guid.NewGuid():N}"));
        // ordinary chat-style executions — must not be counted as form submissions
        db.ExecutionLogs.Add(Log(agent, "completed", now, TimeSpan.FromSeconds(1)));
        db.ExecutionLogs.Add(Log(agent, "failed", now, TimeSpan.FromSeconds(1)));
        await db.SaveChangesAsync();

        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 30);

        Assert.Equal(5, summary.TotalExecutions);
        Assert.Equal(3, summary.FormSubmissions);
        Assert.Equal(1, summary.FormFailedSubmissions);
    }
}
