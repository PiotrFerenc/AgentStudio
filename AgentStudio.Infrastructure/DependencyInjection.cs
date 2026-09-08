using AgentStudio.Application;
using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentStudioInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AgentStudioDbContext>(options =>
        {
            var connectionString = config.GetConnectionString("AgentStudio");
            if (string.IsNullOrWhiteSpace(connectionString) || connectionString == "InMemory")
                options.UseInMemoryDatabase("agentstudio");
            else
                options.UseNpgsql(connectionString);
        });

        services.AddHttpClient("agentstudio-http-tool", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentStudio/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        });

        services.AddMemoryCache();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IExecutionLogRepository, ExecutionLogRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<UserService>();
        services.AddScoped<IConversationStore, PersistentConversationStore>();
        services.AddSingleton<IChatClientFactory, OpenAiCompatibleChatClientFactory>();
        services.AddSingleton<IEmbeddingClientFactory, OpenAiCompatibleEmbeddingClientFactory>();
        services.AddTransient<ISecureHttpExecutor, SecureHttpExecutor>();
        services.AddScoped<IExecutionLogWriter, ExecutionLogWriter>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IDocumentIndexer, DocumentIndexer>();
        services.AddScoped<IDocumentSearchService, DocumentSearchService>();
        services.Configure<List<DatabaseConnectionConfig>>(config.GetSection("DatabaseConnections"));
        services.AddSingleton<IDatabaseConnectionProvider, DatabaseConnectionProvider>();
        services.AddTransient<IDatabaseQueryExecutor, NpgsqlDatabaseQueryExecutor>();
        services.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
        services.Configure<ConversationRetentionOptions>(config.GetSection("ConversationRetention"));
        services.AddHostedService<ConversationRetentionService>();
        services.AddScoped<WorkflowRunner>();
        services.AddScoped<AgentService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        return services;
    }
}
