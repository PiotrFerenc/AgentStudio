using System.Text.Json;
using AgentStudio.Application;
using AgentStudio.Domain;
using Npgsql;

namespace AgentStudio.Infrastructure;

/// <summary>
/// Runs a DatabaseQueryNode's SQL against its named Postgres connection (phase 3). Postgres-only
/// v1 — Npgsql is already a project dependency via Npgsql.EntityFrameworkCore.PostgreSQL, zero
/// new NuGet packages. Query text is never template-expanded (that would make SQL injection
/// trivial) — only the declared Parameters are expanded, then bound as real DbParameters.
/// </summary>
public sealed class NpgsqlDatabaseQueryExecutor : IDatabaseQueryExecutor
{
    // ponytail: 100-row cap with truncation (not a hard fail like the HTTP tool's byte cap) —
    // an over-broad SELECT isn't an attack signal the way an oversized HTTP response can be, so
    // handing back a partial result is more useful than failing the whole node. Raise if a real
    // use case needs more.
    private const int MaxRows = 100;

    private readonly IDatabaseConnectionProvider _connections;

    public NpgsqlDatabaseQueryExecutor(IDatabaseConnectionProvider connections) => _connections = connections;

    public async Task<string> ExecuteAsync(DatabaseQueryNode node, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        var connection = _connections.GetByName(node.ConnectionName)
            ?? throw new InvalidOperationException($"Database connection '{node.ConnectionName}' is not configured.");

        if (!string.Equals(connection.Provider, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Connection '{connection.Name}': provider '{connection.Provider}' is not supported yet — only 'postgres' is implemented.");

        if (connection.ReadOnly && !IsSingleSelect(node.Query))
            throw new InvalidOperationException($"Connection '{connection.Name}' is read-only — only a single SELECT statement is allowed.");

        await using var conn = new NpgsqlConnection(connection.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = node.Query;
        cmd.CommandTimeout = Math.Clamp(node.TimeoutSeconds, 1, 300);

        // Never string-concat a variable into the query text — bind it as a real parameter.
        foreach (var (name, template) in node.Parameters)
        {
            var paramName = name.StartsWith('@') ? name[1..] : name;
            cmd.Parameters.Add(new NpgsqlParameter(paramName, WorkflowRunner.ExpandTemplate(template, variables)));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= MaxRows)
            {
                truncated = true;
                break;
            }
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return JsonSerializer.Serialize(new { rows, rowCount = rows.Count, truncated }, AgentStudioJson.Options);
    }

    /// <summary>Heuristic, not bulletproof — a read-only connection's real protection is the DB
    /// user's own grants; this is a pragmatic guard against an obviously-wrong node config,
    /// matching the HTTP tool's IP-blocklist pragmatism (documented as such, not airtight).</summary>
    private static bool IsSingleSelect(string query)
    {
        var trimmed = query.Trim().TrimEnd(';', ' ', '\t', '\n', '\r');
        return trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase) && !trimmed.Contains(';');
    }
}
