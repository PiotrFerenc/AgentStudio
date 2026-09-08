using System.Net;
using System.Net.Http;
using System.Text;
using AgentStudio.Infrastructure.Integrators;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Exercises the reference IIntegrator implementations at the HTTP-request-building
/// level — URL, auth header, body — via a capturing HttpMessageHandler, without ever sending a
/// real request. There's no sandboxed GitLab/Jira instance to test against for real, so this is
/// the honest substitute: prove the request is built correctly, same spirit as
/// DatabaseQueryExecutorTests proving the SQL is bound correctly.</summary>
public class IntegratorTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return Response;
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    [Fact]
    public async Task GitLab_builds_expected_request_and_parses_response()
    {
        var handler = new CapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"iid":5,"web_url":"https://gitlab.example.com/p/-/issues/5"}""")
            }
        };
        var integrator = new GitLabCreateIssueIntegrator(
            new FakeHttpClientFactory(handler),
            Options.Create(new GitLabIntegratorOptions { BaseUrl = "https://gitlab.example.com", ApiToken = "tok-123" }));

        var config = new Dictionary<string, string> { ["projectId"] = "42", ["title"] = "Bug found", ["description"] = "details" };
        var result = await integrator.ExecuteAsync(config, new Dictionary<string, string>());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/42/issues", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("tok-123", handler.LastRequest.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Contains("\"title\":\"Bug found\"", handler.LastRequestBody);
        Assert.Contains("5", result);
        Assert.Contains("gitlab.example.com/p/-/issues/5", result);
    }

    [Fact]
    public async Task GitLab_missing_projectId_fails_clearly()
    {
        var integrator = new GitLabCreateIssueIntegrator(
            new FakeHttpClientFactory(new CapturingHandler()),
            Options.Create(new GitLabIntegratorOptions { BaseUrl = "https://gitlab.example.com", ApiToken = "tok" }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            integrator.ExecuteAsync(new Dictionary<string, string> { ["title"] = "x" }, new Dictionary<string, string>()));
        Assert.Contains("projectId", ex.Message);
    }

    [Fact]
    public async Task GitLab_unconfigured_fails_clearly()
    {
        var integrator = new GitLabCreateIssueIntegrator(
            new FakeHttpClientFactory(new CapturingHandler()),
            Options.Create(new GitLabIntegratorOptions()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            integrator.ExecuteAsync(new Dictionary<string, string> { ["projectId"] = "1", ["title"] = "x" }, new Dictionary<string, string>()));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task Jira_builds_expected_request_with_basic_auth()
    {
        var handler = new CapturingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"key":"PROJ-7","self":"https://your.atlassian.net/rest/api/3/issue/70007"}""")
            }
        };
        var integrator = new JiraCreateIssueIntegrator(
            new FakeHttpClientFactory(handler),
            Options.Create(new JiraIntegratorOptions { BaseUrl = "https://your.atlassian.net", Email = "bot@example.com", ApiToken = "tok-456" }));

        var config = new Dictionary<string, string> { ["projectKey"] = "PROJ", ["summary"] = "New task", ["description"] = "details" };
        var result = await integrator.ExecuteAsync(config, new Dictionary<string, string>());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://your.atlassian.net/rest/api/3/issue", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Basic", handler.LastRequest.Headers.Authorization!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastRequest.Headers.Authorization.Parameter!));
        Assert.Equal("bot@example.com:tok-456", decoded);
        Assert.Contains("\"summary\":\"New task\"", handler.LastRequestBody);
        Assert.Contains("\"key\":\"PROJ\"", handler.LastRequestBody);
        Assert.Contains("PROJ-7", result);
    }

    [Fact]
    public async Task Jira_unconfigured_fails_clearly()
    {
        var integrator = new JiraCreateIssueIntegrator(
            new FakeHttpClientFactory(new CapturingHandler()),
            Options.Create(new JiraIntegratorOptions()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            integrator.ExecuteAsync(new Dictionary<string, string> { ["projectKey"] = "P", ["summary"] = "x" }, new Dictionary<string, string>()));
        Assert.Contains("not configured", ex.Message);
    }
}
