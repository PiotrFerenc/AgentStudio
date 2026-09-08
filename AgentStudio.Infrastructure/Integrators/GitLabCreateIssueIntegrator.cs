using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Application;
using Microsoft.Extensions.Options;

namespace AgentStudio.Infrastructure.Integrators;

/// <summary>Base URL and PAT come from appsettings.json ("Integrators:GitLab") — deployment
/// config, not editable through the UI, same split as DatabaseConnections.</summary>
public sealed class GitLabIntegratorOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
}

/// <summary>Reference IIntegrator implementation (phase 4) — creates a GitLab issue.
/// Per-call values (project, title, description) come from IntegratorNode.Config, already
/// template-expanded by WorkflowRunner before this runs.</summary>
public sealed class GitLabCreateIssueIntegrator : IIntegrator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitLabIntegratorOptions _options;

    public GitLabCreateIssueIntegrator(IHttpClientFactory httpClientFactory, IOptions<GitLabIntegratorOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string Name => "gitlab.create-issue";
    public string Description => "Creates an issue in a GitLab project.";

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new InvalidOperationException("gitlab.create-issue is not configured — set Integrators:GitLab:BaseUrl and ApiToken.");

        var projectId = config.GetValueOrDefault("projectId")
            ?? throw new InvalidOperationException("gitlab.create-issue requires a 'projectId' config value.");
        var title = config.GetValueOrDefault("title")
            ?? throw new InvalidOperationException("gitlab.create-issue requires a 'title' config value.");
        var descriptionText = config.GetValueOrDefault("description", "");

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/v4/projects/{Uri.EscapeDataString(projectId)}/issues";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { title, description = descriptionText })
        };
        request.Headers.Add("PRIVATE-TOKEN", _options.ApiToken);

        var client = _httpClientFactory.CreateClient("agentstudio-http-tool");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitLab create issue failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var iid = doc.RootElement.TryGetProperty("iid", out var iidProp) ? iidProp.GetInt32() : (int?)null;
        var webUrl = doc.RootElement.TryGetProperty("web_url", out var urlProp) ? urlProp.GetString() : null;
        return JsonSerializer.Serialize(new { iid, webUrl });
    }
}
