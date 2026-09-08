using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application;
using Microsoft.Extensions.Options;

namespace AgentStudio.Infrastructure.Integrators;

/// <summary>Base URL and auth (email + API token, Jira Cloud's basic-auth scheme) come from
/// appsettings.json ("Integrators:Jira") — deployment config, not editable through the UI, same
/// split as DatabaseConnections.</summary>
public sealed class JiraIntegratorOptions
{
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string ApiToken { get; set; } = "";
}

/// <summary>Reference IIntegrator implementation (phase 4) — creates a Jira issue via the
/// REST API v3. Per-call values (project, summary, description, issue type) come from
/// IntegratorNode.Config, already template-expanded by WorkflowRunner before this runs.</summary>
public sealed class JiraCreateIssueIntegrator : IIntegrator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JiraIntegratorOptions _options;

    public JiraCreateIssueIntegrator(IHttpClientFactory httpClientFactory, IOptions<JiraIntegratorOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string Name => "jira.create-issue";
    public string Description => "Creates an issue in a Jira project.";

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.Email) || string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new InvalidOperationException("jira.create-issue is not configured — set Integrators:Jira:BaseUrl, Email and ApiToken.");

        var projectKey = config.GetValueOrDefault("projectKey")
            ?? throw new InvalidOperationException("jira.create-issue requires a 'projectKey' config value.");
        var summary = config.GetValueOrDefault("summary")
            ?? throw new InvalidOperationException("jira.create-issue requires a 'summary' config value.");
        var descriptionText = config.GetValueOrDefault("description", "");
        var issueType = config.GetValueOrDefault("issueType", "Task");

        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["summary"] = summary,
            ["issuetype"] = new JsonObject { ["name"] = issueType }
        };
        // Jira Cloud v3 requires Atlassian Document Format for rich text, and rejects an empty
        // "text" node — so description is only included when there's something to say, rather
        // than always sending a paragraph containing "".
        if (!string.IsNullOrWhiteSpace(descriptionText))
        {
            fields["description"] = new JsonObject
            {
                ["type"] = "doc",
                ["version"] = 1,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = descriptionText } }
                    }
                }
            };
        }
        var payload = new JsonObject { ["fields"] = fields };

        var url = $"{_options.BaseUrl.TrimEnd('/')}/rest/api/3/issue";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        var client = _httpClientFactory.CreateClient("agentstudio-http-tool");
        var body = await IntegratorHttp.SendAsync(client, request, "Jira create issue", ct);

        using var doc = JsonDocument.Parse(body);
        var key = doc.RootElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
        var self = doc.RootElement.TryGetProperty("self", out var selfProp) ? selfProp.GetString() : null;
        return JsonSerializer.Serialize(new { key, self });
    }
}
