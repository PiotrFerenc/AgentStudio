using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.Extensions.Options;

namespace AgentStudio.Infrastructure;

/// <summary>Reads database connections from the "DatabaseConnections" config section
/// (appsettings.json — an array of DatabaseConnectionConfig) instead of a database table.</summary>
public sealed class DatabaseConnectionProvider : IDatabaseConnectionProvider
{
    private readonly IOptions<List<DatabaseConnectionConfig>> _options;

    public DatabaseConnectionProvider(IOptions<List<DatabaseConnectionConfig>> options) => _options = options;

    public List<DatabaseConnectionConfig> List() => _options.Value;

    public DatabaseConnectionConfig? GetByName(string name) =>
        _options.Value.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
