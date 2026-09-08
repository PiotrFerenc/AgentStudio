using System.Text.Json;
using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Exercises NpgsqlDatabaseQueryExecutor against a real Postgres — the same instance
/// set up for persistence (see PLAN.md faza 2, "dodaj zapis w bazie": localhost:5433). The
/// injection/read-only/truncation guards can't be meaningfully tested against a fake — they're
/// about what actually gets sent to and executed by a real SQL engine.</summary>
public class DatabaseQueryExecutorTests
{
    private const string TestConnectionString =
        "Host=localhost;Port=5433;Database=agentstudio;Username=agentstudio;Password=agentstudio";

    private sealed class FixedConnectionProvider : IDatabaseConnectionProvider
    {
        private readonly DatabaseConnectionConfig _config;
        public FixedConnectionProvider(DatabaseConnectionConfig config) => _config = config;

        public List<DatabaseConnectionConfig> List() => new() { _config };
        public DatabaseConnectionConfig? GetByName(string name) => name == _config.Name ? _config : null;
    }

    private static (NpgsqlDatabaseQueryExecutor Executor, string ConnectionName) NewExecutor(bool readOnly, string provider = "postgres")
    {
        var config = new DatabaseConnectionConfig { Name = "test-conn", ConnectionString = TestConnectionString, ReadOnly = readOnly, Provider = provider };
        return (new NpgsqlDatabaseQueryExecutor(new FixedConnectionProvider(config)), config.Name);
    }

    [Fact]
    public async Task Parameterized_value_is_bound_as_data_never_executed_as_sql()
    {
        var (executor, connName) = NewExecutor(readOnly: true);
        var node = new DatabaseQueryNode
        {
            Id = "q",
            ConnectionName = connName,
            Query = "SELECT @val AS result",
            Parameters = new Dictionary<string, string> { ["val"] = "{variables.malicious}" },
            ResultVariable = "r"
        };
        const string malicious = "'; DROP TABLE pg_stat_activity; --";
        var variables = new Dictionary<string, string> { ["malicious"] = malicious };

        var resultJson = await executor.ExecuteAsync(node, variables);

        using var doc = JsonDocument.Parse(resultJson);
        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        // If this had been concatenated into the query instead of bound as a parameter, this
        // exact literal string would never come back as a single scalar value.
        Assert.Equal(malicious, rows[0].GetProperty("result").GetString());
    }

    [Fact]
    public async Task Non_select_statement_rejected_on_readonly_connection()
    {
        var (executor, connName) = NewExecutor(readOnly: true);
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = connName, Query = "DELETE FROM pg_stat_activity", ResultVariable = "r" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(node, new Dictionary<string, string>()));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task Multi_statement_query_rejected_on_readonly_connection()
    {
        var (executor, connName) = NewExecutor(readOnly: true);
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = connName, Query = "SELECT 1; DELETE FROM pg_stat_activity", ResultVariable = "r" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(node, new Dictionary<string, string>()));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task Write_connection_allows_non_select()
    {
        var (executor, connName) = NewExecutor(readOnly: false);
        // A harmless no-op statement — proves the read-only guard is what's being tested, not
        // just "any DELETE always throws" for unrelated reasons (e.g. missing table).
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = connName, Query = "SELECT 1 WHERE false", ResultVariable = "r" };

        var resultJson = await executor.ExecuteAsync(node, new Dictionary<string, string>());

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal(0, doc.RootElement.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task Results_are_truncated_beyond_the_row_cap()
    {
        var (executor, connName) = NewExecutor(readOnly: true);
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = connName, Query = "SELECT * FROM generate_series(1, 150) AS n", ResultVariable = "r" };

        var resultJson = await executor.ExecuteAsync(node, new Dictionary<string, string>());

        using var doc = JsonDocument.Parse(resultJson);
        Assert.Equal(100, doc.RootElement.GetProperty("rowCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Unsupported_provider_throws_clear_error()
    {
        var (executor, connName) = NewExecutor(readOnly: true, provider: "mysql");
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = connName, Query = "SELECT 1", ResultVariable = "r" };

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync(node, new Dictionary<string, string>()));
        Assert.Contains("mysql", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_connection_throws_clear_error()
    {
        var (executor, _) = NewExecutor(readOnly: true);
        var node = new DatabaseQueryNode { Id = "q", ConnectionName = "does-not-exist", Query = "SELECT 1", ResultVariable = "r" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(node, new Dictionary<string, string>()));
        Assert.Contains("not configured", ex.Message);
    }
}
