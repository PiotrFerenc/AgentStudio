using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure.Integrators;
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
        services.Configure<List<ModelProviderConfig>>(config.GetSection("ModelProviders"));
        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IGraphComponentRepository, GraphComponentRepository>();
        services.AddScoped<IExecutionLogRepository, ExecutionLogRepository>();
        services.Configure<List<UserConfig>>(config.GetSection("Users"));
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
        services.AddScoped<IAgentCollectionStore, EfAgentCollectionStore>();
        services.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
        services.Configure<GitLabIntegratorOptions>(config.GetSection("Integrators:GitLab"));
        services.AddTransient<IIntegrator, GitLabCreateIssueIntegrator>();
        services.Configure<JiraIntegratorOptions>(config.GetSection("Integrators:Jira"));
        services.AddTransient<IIntegrator, JiraCreateIssueIntegrator>();
        services.Configure<ConversationRetentionOptions>(config.GetSection("ConversationRetention"));
        services.AddHostedService<ConversationRetentionService>();
        services.Configure<ScheduledRunnerOptions>(config.GetSection("ScheduledRunner"));
        services.AddHostedService<ScheduledRunner>();
        services.AddScoped<WorkflowRunner>();
        services.AddScoped<AgentService>();
        services.AddScoped<GraphGenerationService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        return services;
    }
}
