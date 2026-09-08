using AgentStudio.Application;
using AgentStudio.Domain;

namespace AgentStudio.Infrastructure;

public sealed class ExecutionLogWriter : IExecutionLogWriter
{
    private readonly IExecutionLogRepository _logs;
    private readonly List<ExecutionLog> _pending = new();

    public ExecutionLogWriter(IExecutionLogRepository logs) => _logs = logs;

    public ExecutionLog Start(string conversationId, Guid agentId, int agentVersion)
    {
        var log = new ExecutionLog
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            AgentId = agentId,
            AgentVersion = agentVersion
        };
        _pending.Add(log);
        return log;
    }

    public ExecutionStep StartStep(ExecutionLog log, string nodeId, string nodeType)
    {
        var step = new ExecutionStep { NodeId = nodeId, NodeType = nodeType };
        // Parallel branches (phase 2) call this concurrently on the same log — List<T>.Add is not
        // thread-safe. CompleteStep/FailStep need no lock: each mutates only its own step object.
        lock (log)
            log.Steps.Add(step);
        return step;
    }

    public void CompleteStep(ExecutionStep step, string? detail = null)
    {
        step.Status = "completed";
        step.CompletedAt = DateTimeOffset.UtcNow;
        step.Detail = detail;
    }

    public void FailStep(ExecutionStep step, string error)
    {
        step.Status = "failed";
        step.CompletedAt = DateTimeOffset.UtcNow;
        step.Error = error;
    }

    public async Task CompleteAsync(ExecutionLog log, string? error = null, CancellationToken ct = default)
    {
        log.CompletedAt = DateTimeOffset.UtcNow;
        log.Status = error is null ? "completed" : "failed";
        log.Error = error;
        await _logs.AddAsync(log, ct);
        await _logs.SaveChangesAsync(ct);
        _pending.Remove(log);
    }
}
