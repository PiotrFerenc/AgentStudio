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
    public async Task GetSummaryAsync_ByDay_is_zero_filled_across_a_gap_with_no_executions()
    {
        // Executions three days apart with nothing in between — ByDay must still contain an
        // entry for every day in between (Executions=0), not just the two days that had data.
        // A sparse list here would make the "Executions per day" chart compress the gap away,
        // drawing the two active days as if they were adjacent.
        var db = NewDb("analytics-byday-gap");
        var agent = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agent, Name = "Agent" });
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        db.ExecutionLogs.Add(Log(agent, "completed", today.AddDays(-4), TimeSpan.FromSeconds(1)));
        db.ExecutionLogs.Add(Log(agent, "completed", today.AddDays(-1), TimeSpan.FromSeconds(1)));
        await db.SaveChangesAsync();

        var repo = new AnalyticsRepository(db);
        var summary = await repo.GetSummaryAsync(days: 7);

        var byDate = summary.ByDay.ToDictionary(d => d.Date, d => d.Executions);
        Assert.Equal(1, byDate[DateOnly.FromDateTime(today.AddDays(-4).UtcDateTime)]);
        Assert.Equal(0, byDate[DateOnly.FromDateTime(today.AddDays(-3).UtcDateTime)]);
        Assert.Equal(0, byDate[DateOnly.FromDateTime(today.AddDays(-2).UtcDateTime)]);
        Assert.Equal(1, byDate[DateOnly.FromDateTime(today.AddDays(-1).UtcDateTime)]);
        // Consecutive calendar days, no skipped dates anywhere in the returned list.
        var ordered = summary.ByDay.OrderBy(d => d.Date).Select(d => d.Date).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].AddDays(1), ordered[i]);
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
        // ByDay is zero-filled for the whole window (not empty) — see the next test.
        Assert.All(summary.ByDay, d => Assert.Equal(0, d.Executions));
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
