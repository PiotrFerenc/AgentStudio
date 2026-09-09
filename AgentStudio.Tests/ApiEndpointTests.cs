using System.Net;
using System.Net.Http.Json;
using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Exercises the real REST endpoints in Program.cs (routing, auth, status codes) end-to-end
/// over an in-memory test server, isolated from the runner/network so no LLM call is required.
/// The management /api group requires a cookie-authenticated session (phase 2, etap 1) — each
/// test logs in as a bootstrap admin before exercising the endpoints under test.</summary>
public sealed class ApiEndpointTests : IClassFixture<ApiEndpointTests.Factory>, IAsyncLifetime
{
    private const string AdminUsername = "test-admin";
    private const string AdminPassword = "test-password-123";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // appsettings.Development.json points at a real Postgres for local `dotnet run`.
            // Override it back to InMemory here — added *before* ConfigureServices runs, so
            // Program.cs's AddAgentStudioInfrastructure(builder.Configuration) never sees the
            // Postgres string and never registers the Npgsql provider (registering both
            // providers in one service collection throws at DbContext resolution time).
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AgentStudio"] = "InMemory"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AgentStudioDbContext>>();
                services.AddDbContext<AgentStudioDbContext>(o => o.UseInMemoryDatabase(_dbName));
            });
        }
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ApiEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        // Idempotent: the Factory's DB (and its admin account) is shared across this class's
        // tests, but each test gets a fresh HttpClient/cookie jar, so each logs in separately.
        var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AdminUsername,
            ["password"] = AdminPassword
        });
        await _client.PostAsync("/auth/setup", credentials);
        var login = await _client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = AdminUsername,
            ["password"] = AdminPassword
        }));
        login.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid Id, string ApiKey)> CreateAgentAsync(string name = "smoke", string providerName = "provider-x")
    {
        var resp = await _client.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest(name, "d", "instructions", providerName, "model-x"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CreateAgentResponse>();
        return (body!.Agent.Id, body.ApiKey);
    }

    private sealed record CreateAgentResponse(AgentDto Agent, string ApiKey);

    /// <summary>Creates a fresh non-admin Editor and returns a logged-in client for them —
    /// distinct from the shared admin _client, needed to exercise per-agent access control
    /// (phase 14), since an Admin bypasses ownership entirely.</summary>
    private async Task<HttpClient> LoginAsNewEditorAsync(string username)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateAsync(username, "editor-password-123", UserRole.Editor);
        }
        var client = _factory.CreateClient();
        var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = "editor-password-123"
        }));
        login.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Create_and_get_agent_roundtrips()
    {
        var (id, _) = await CreateAgentAsync();

        var getResp = await _client.GetAsync($"/api/agents/{id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
    }

    [Fact]
    public async Task Management_endpoint_without_login_returns_401()
    {
        // A fresh, never-logged-in client — distinct from the shared _client, which
        // IAsyncLifetime.InitializeAsync always authenticates before each test runs.
        using var anonymous = _factory.CreateClient();

        var resp = await anonymous.GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_agent_returns_404()
    {
        var resp = await _client.GetAsync($"/api/agents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Draft_update_and_publish_roundtrip()
    {
        var (id, _) = await CreateAgentAsync("publish-flow");
        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        var draftResp = await _client.PutAsJsonAsync($"/api/agents/{id}/draft", new UpdateDraftRequest(graph));
        Assert.Equal(HttpStatusCode.OK, draftResp.StatusCode);
        var validation = await draftResp.Content.ReadFromJsonAsync<ValidationResultDto>();
        Assert.True(validation!.IsValid);

        var publishResp = await _client.PostAsync($"/api/agents/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResp.StatusCode);
        var version = await publishResp.Content.ReadFromJsonAsync<AgentVersionDto>();
        Assert.Equal(1, version!.Version);
    }

    [Fact]
    public async Task Publish_without_valid_draft_returns_400()
    {
        var (id, _) = await CreateAgentAsync("bad-publish");
        // Default draft has a dangling prompt node with no End reachable via edges beyond default graph;
        // force an invalid graph explicitly (Start with no End).
        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        await _client.PutAsJsonAsync($"/api/agents/{id}/draft", new UpdateDraftRequest(graph));

        var publishResp = await _client.PostAsync($"/api/agents/{id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, publishResp.StatusCode);
    }

    private async Task<(Guid Id, string ApiKey)> CreatePublishedAgentAsync(string name)
    {
        var (id, apiKey) = await CreateAgentAsync(name);
        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
        await _client.PutAsJsonAsync($"/api/agents/{id}/draft", new UpdateDraftRequest(graph));
        await _client.PostAsync($"/api/agents/{id}/publish", null);
        return (id, apiKey);
    }

    [Fact]
    public async Task Conversation_without_api_key_returns_401()
    {
        var (id, _) = await CreatePublishedAgentAsync("no-key");

        var resp = await _client.PostAsJsonAsync($"/api/agents/{id}/versions/1/conversations",
            new ConversationRequest("hi", null));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Conversation_with_wrong_api_key_returns_401()
    {
        var (id, _) = await CreatePublishedAgentAsync("wrong-key");

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/versions/1/conversations")
        {
            Content = JsonContent.Create(new ConversationRequest("hi", null))
        };
        req.Headers.Add("X-Agent-Api-Key", "ask_totally-wrong");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Conversation_for_unpublished_version_returns_404()
    {
        var (id, apiKey) = await CreateAgentAsync("never-published");

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/versions/1/conversations")
        {
            Content = JsonContent.Create(new ConversationRequest("hi", null))
        };
        req.Headers.Add("X-Agent-Api-Key", apiKey);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Conversation_with_valid_key_but_no_provider_returns_400()
    {
        // CreateAgentAsync points at a model provider name that was never registered via /api/providers.
        var (id, apiKey) = await CreatePublishedAgentAsync("no-provider");

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/versions/1/conversations")
        {
            Content = JsonContent.Create(new ConversationRequest("hi", null))
        };
        req.Headers.Add("X-Agent-Api-Key", apiKey);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Stream_endpoint_emits_sse_events_end_to_end()
    {
        // A Message node never calls the chat client, but ResolveRuntimeAsync still requires
        // the agent's provider to be configured, so register one (its URL is never dialed).
        // Uses a name distinct from "provider-x" so it doesn't leak into the "no provider" test.
        await _client.PostAsJsonAsync("/api/providers", new CreateProviderRequest("provider-stream", "http://unused.invalid", "model-x", null));
        var (id, apiKey) = await CreateAgentAsync("stream-flow", "provider-stream");
        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "m", Type = "message", Props = new() { ["text"] = "Hello streaming world" } });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "m" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "2", SourceNodeId = "m", TargetNodeId = "e" });
        await _client.PutAsJsonAsync($"/api/agents/{id}/draft", new UpdateDraftRequest(graph));
        await _client.PostAsync($"/api/agents/{id}/publish", null);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/versions/1/stream")
        {
            Content = JsonContent.Create(new ConversationRequest("hi", null))
        };
        req.Headers.Add("X-Agent-Api-Key", apiKey);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("event: conversation", body);
        Assert.Contains("\"delta\":\"Hello streaming world\"", body);
        Assert.Contains("event: done", body);
    }

    [Fact]
    public async Task Non_owner_editor_cannot_edit_someone_elses_agent()
    {
        // Created by the admin _client, so its owner is the admin, not "eve".
        var (id, _) = await CreateAgentAsync("owned-by-admin");
        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });

        var eve = await LoginAsNewEditorAsync("eve");
        var resp = await eve.PutAsJsonAsync($"/api/agents/{id}/draft", new UpdateDraftRequest(graph));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Owner_can_edit_their_own_agent_even_without_admin_role()
    {
        var frank = await LoginAsNewEditorAsync("frank");
        await frank.PostAsJsonAsync("/api/providers", new CreateProviderRequest("provider-frank", "http://unused.invalid", "model-x", null));
        var createResp = await frank.PostAsJsonAsync("/api/agents", new CreateAgentRequest("franks-agent", "d", "i", "provider-frank", "model-x"));
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<CreateAgentResponse>();

        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
        var draftResp = await frank.PutAsJsonAsync($"/api/agents/{created!.Agent.Id}/draft", new UpdateDraftRequest(graph));

        Assert.Equal(HttpStatusCode.OK, draftResp.StatusCode);
    }

    [Fact]
    public async Task Admin_can_edit_an_agent_owned_by_someone_else()
    {
        var grace = await LoginAsNewEditorAsync("grace");
        await grace.PostAsJsonAsync("/api/providers", new CreateProviderRequest("provider-grace", "http://unused.invalid", "model-x", null));
        var createResp = await grace.PostAsJsonAsync("/api/agents", new CreateAgentRequest("graces-agent", "d", "i", "provider-grace", "model-x"));
        var created = await createResp.Content.ReadFromJsonAsync<CreateAgentResponse>();

        var graph = new WorkflowGraphDto();
        graph.Nodes.Add(new WorkflowNodeDto { Id = "s", Type = "start" });
        graph.Nodes.Add(new WorkflowNodeDto { Id = "e", Type = "end" });
        graph.Edges.Add(new WorkflowEdgeDto { Id = "1", SourceNodeId = "s", TargetNodeId = "e" });
        // _client is the bootstrap Admin — not grace's agent, but Admin bypasses ownership.
        var draftResp = await _client.PutAsJsonAsync($"/api/agents/{created!.Agent.Id}/draft", new UpdateDraftRequest(graph));

        Assert.Equal(HttpStatusCode.OK, draftResp.StatusCode);
    }
}
