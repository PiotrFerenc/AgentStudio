using System.Security.Claims;
using System.Text;
using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using AgentStudio.Web.Components;
using AgentStudio.Web.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAgentStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<StudioApiClient>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthRateLimiting.PolicyName, AuthRateLimiting.GetPartition);
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        // /api is a JSON API (curl, widget, fetch) — an unauthenticated call there should get a
        // plain 401, not a redirect to the HTML login page.
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentStudioDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// /api is a JSON API — never re-execute its error responses against a Razor Components page
// (that re-execution replays the original request, including its JSON body, into a page whose
// POST handling requires antiforgery-validated form data, turning a clean 401/404 into a 400).
// /auth is plain form posts/redirects for the same reason, and additionally must let a 429 from
// the login rate limiter reach the client as 429 — re-execution against /not-found silently
// turned it into a 404, hiding from the client (and from logs) that it was actually throttled.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/auth"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseAntiforgery();

// ============================
// Auth (cookie-based, phase 2). Login is a plain form post so Set-Cookie lands on a real HTTP
// response — not routed through a Blazor Interactive Server circuit. No /setup: accounts are
// entirely appsettings.json's "Users" array now, nothing to bootstrap here.
// ============================

var auth = app.MapGroup("/auth").DisableAntiforgery().AllowAnonymous();

auth.MapPost("/login", async (HttpContext http, UserService users, CancellationToken ct) =>
{
    var form = await http.Request.ReadFormAsync(ct);
    var user = await users.VerifyPasswordAsync(form["username"].ToString(), form["password"].ToString(), ct);
    if (user is null)
        return Results.Redirect("/login?error=1");

    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).RequireRateLimiting(AuthRateLimiting.PolicyName);

auth.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// ============================
// Studio management API (cookie-authenticated — phase 2)
// ============================

var api = app.MapGroup("/api").DisableAntiforgery().RequireAuthorization();

api.MapGet("/providers", async (IProviderRepository repo, CancellationToken ct) =>
    Results.Ok((await repo.ListAsync(ct)).Select(p => new ProviderDto(p.Id, p.Name, p.BaseUrl, p.DefaultModel, p.ApiKey is not null, p.EmbeddingModel, p.Headers))));

api.MapGet("/agents", async (IAgentRepository repo, CancellationToken ct) =>
    Results.Ok((await repo.ListAsync(ct)).Select(a => new AgentDto(a.Id, a.Name, a.Description, a.SystemInstructions, a.ModelProviderName, a.ModelName, a.CreatedAt))));

// Per-agent edit access (phase 14): Admin always allowed; otherwise the agent's Owner or a
// listed Collaborator. Shared by every mutating /agents/{id}/... endpoint below so a curl with
// a valid session cookie can't bypass the same check the Blazor UI applies — client-side-only
// enforcement is trivially bypassed, same reasoning as every other guard in this API.
static async Task<(Agent? Agent, IResult? Error)> CheckEditAccessAsync(Guid agentId, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct)
{
    var agent = await agents.GetAsync(agentId, ct);
    if (agent is null) return (null, Results.NotFound());
    if (!Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        return (null, Results.Json(new { error = "Not authenticated." }, statusCode: 401));
    var user = await users.GetAsync(userId, ct);
    if (user is null || !AgentAccess.CanEdit(agent, user))
        return (null, Results.Json(new { error = "You don't have edit access to this agent." }, statusCode: 403));
    return (agent, null);
}

api.MapPost("/agents", async (CreateAgentRequest req, AgentService service, HttpContext http, CancellationToken ct) =>
{
    Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var ownerId);
    var ownerUsername = http.User.FindFirst(ClaimTypes.Name)?.Value;
    var (agent, rawKey) = await service.CreateAsync(req, ownerId, ownerUsername, ct: ct);
    return Results.Created($"/api/agents/{agent.Id}", new
    {
        agent = new AgentDto(agent.Id, agent.Name, agent.Description, agent.SystemInstructions, agent.ModelProviderName, agent.ModelName, agent.CreatedAt),
        apiKey = rawKey
    });
});

api.MapGet("/agents/{id:guid}", async (Guid id, IAgentRepository repo, CancellationToken ct) =>
{
    var agent = await repo.GetAsync(id, ct);
    return agent is null ? Results.NotFound() : Results.Ok(new
    {
        agent = new AgentDto(agent.Id, agent.Name, agent.Description, agent.SystemInstructions, agent.ModelProviderName, agent.ModelName, agent.CreatedAt),
        draft = agent.Draft is null ? null : GraphMapper.ToDto(agent.Draft.Graph),
        draftStatus = agent.Draft?.Status.ToString(),
        versions = agent.Versions.Select(v => new AgentVersionDto(v.Version, v.Status.ToString(), v.CreatedAt, v.PublishedAt, v.MaxSteps))
    });
});

api.MapPut("/agents/{id:guid}/draft", async (Guid id, UpdateDraftRequest req, AgentService service, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct) =>
{
    var (_, accessError) = await CheckEditAccessAsync(id, http, agents, users, ct);
    if (accessError is not null) return accessError;
    try
    {
        return Results.Ok(await service.UpdateDraftAsync(id, req.Graph, req.MaxSteps, req.FormFields, req.FormResultMode, req.FormResultTarget, req.FormResultMarkdown, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

api.MapPost("/agents/{id:guid}/validate", async (Guid id, IAgentRepository repo, CancellationToken ct) =>
{
    var agent = await repo.GetAsync(id, ct);
    if (agent?.Draft is null) return Results.NotFound();
    var errors = WorkflowValidator.Validate(agent.Draft.Graph);
    return Results.Ok(new ValidationResultDto(errors.Count == 0, errors));
});

api.MapPost("/agents/{id:guid}/publish", async (Guid id, AgentService service, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct) =>
{
    var (_, accessError) = await CheckEditAccessAsync(id, http, agents, users, ct);
    if (accessError is not null) return accessError;
    try
    {
        var version = await service.PublishAsync(id, ct);
        return Results.Ok(new AgentVersionDto(version.Version, version.Status.ToString(), version.CreatedAt, version.PublishedAt, version.MaxSteps));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/agents/{id:guid}/regenerate-key", async (Guid id, AgentService service, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct) =>
{
    var (_, accessError) = await CheckEditAccessAsync(id, http, agents, users, ct);
    if (accessError is not null) return accessError;
    try
    {
        var rawKey = await service.RegenerateApiKeyAsync(id, ct);
        return Results.Ok(new { apiKey = rawKey });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

api.MapPost("/agents/{id:guid}/versions/{version:int}/unpublish", async (Guid id, int version, AgentService service, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct) =>
{
    var (_, accessError) = await CheckEditAccessAsync(id, http, agents, users, ct);
    if (accessError is not null) return accessError;
    try
    {
        var v = await service.UnpublishAsync(id, version, ct);
        return Results.Ok(new AgentVersionDto(v.Version, v.Status.ToString(), v.CreatedAt, v.PublishedAt, v.MaxSteps));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/agents/{id:guid}/versions/{version:int}/republish", async (Guid id, int version, AgentService service, HttpContext http, IAgentRepository agents, IUserRepository users, CancellationToken ct) =>
{
    var (_, accessError) = await CheckEditAccessAsync(id, http, agents, users, ct);
    if (accessError is not null) return accessError;
    try
    {
        var v = await service.RepublishAsync(id, version, ct);
        return Results.Ok(new AgentVersionDto(v.Version, v.Status.ToString(), v.CreatedAt, v.PublishedAt, v.MaxSteps));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/agents/{id:guid}/versions", async (Guid id, IAgentRepository repo, CancellationToken ct) =>
{
    var agent = await repo.GetAsync(id, ct);
    return agent is null
        ? Results.NotFound()
        : Results.Ok(agent.Versions.Select(v => new AgentVersionDto(v.Version, v.Status.ToString(), v.CreatedAt, v.PublishedAt, v.MaxSteps)));
});

api.MapGet("/agents/{id:guid}/logs", async (Guid id, IExecutionLogRepository repo, CancellationToken ct) =>
{
    var logs = await repo.ListForAgentAsync(id, 50, ct);
    return Results.Ok(logs.Select(l => new ExecutionLogDto(l.ExecutionId, l.ConversationId, l.Status, l.StartedAt, l.CompletedAt, l.Error,
        l.Steps.Select(s => new ExecutionStepDto(s.NodeId, s.NodeType, s.Status, s.StartedAt, s.CompletedAt, s.Detail, s.Error)).ToList())));
});

// ============================
// Published agent runtime API (API key protected, not cookie auth — public by design)
// ============================

var runtimeApi = app.MapGroup("/api").DisableAntiforgery().AllowAnonymous();

static async Task<(Agent? Agent, AgentVersion? Version, ModelProviderConfig? Provider, IResult? Error)> ResolveRuntimeAsync(
    Guid agentId, int version, string? apiKey,
    IAgentRepository agents, IProviderRepository providers, AgentService service, CancellationToken ct)
{
    var agent = await agents.GetAsync(agentId, ct);
    if (agent is null) return (null, null, null, Results.NotFound(new { error = "Agent not found." }));

    if (!service.VerifyApiKey(agent, apiKey))
        return (null, null, null, Results.Json(new { error = "Invalid or missing API key." }, statusCode: 401));

    var published = agent.Versions.FirstOrDefault(v => v.Version == version && v.Status == AgentVersionStatus.Published);
    if (published is null) return (null, null, null, Results.NotFound(new { error = "Published version not found." }));

    var provider = await providers.GetByNameAsync(agent.ModelProviderName, ct);
    if (provider is null) return (null, null, null, Results.BadRequest(new { error = $"Model provider '{agent.ModelProviderName}' is not configured." }));

    return (agent, published, provider, null);
}

runtimeApi.MapPost("/agents/{id:guid}/versions/{version:int}/conversations", async (
    Guid id, int version, ConversationRequest req, HttpContext http,
    IAgentRepository agents, IProviderRepository providers, AgentService service,
    IConversationStore conversations, WorkflowRunner runner, CancellationToken ct) =>
{
    var apiKey = http.Request.Headers["X-Agent-Api-Key"].FirstOrDefault();
    var (agent, published, provider, error) = await ResolveRuntimeAsync(id, version, apiKey, agents, providers, service, ct);
    if (error is not null) return error;

    var conversation = conversations.GetOrCreate(req.ConversationId, agent!.Id, published!.Version);
    var reply = new StringBuilder();
    await foreach (var chunk in runner.RunAsync(agent, published, provider!, conversation, req.Message, ct))
        reply.Append(chunk);
    conversations.Save(conversation);
    return Results.Ok(new ConversationResponse(conversation.ConversationId, reply.ToString(), runner.LastExecutionId ?? ""));
}).DisableAntiforgery();

runtimeApi.MapPost("/agents/{id:guid}/versions/{version:int}/stream", async (
    Guid id, int version, ConversationRequest req, HttpContext http,
    IAgentRepository agents, IProviderRepository providers, AgentService service,
    IConversationStore conversations, WorkflowRunner runner, CancellationToken ct) =>
{
    var apiKey = http.Request.Headers["X-Agent-Api-Key"].FirstOrDefault();
    var (agent, published, provider, error) = await ResolveRuntimeAsync(id, version, apiKey, agents, providers, service, ct);
    if (error is not null) return error;

    var conversation = conversations.GetOrCreate(req.ConversationId, agent!.Id, published!.Version);

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    await http.Response.WriteAsync($"event: conversation\ndata: {{\"conversationId\":\"{conversation.ConversationId}\"}}\n\n", ct);

    await foreach (var chunk in runner.RunAsync(agent, published, provider!, conversation, req.Message, ct))
    {
        var data = System.Text.Json.JsonSerializer.Serialize(new { delta = chunk });
        await http.Response.WriteAsync($"data: {data}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    conversations.Save(conversation);
    await http.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
    return Results.Empty;
}).DisableAntiforgery();

// Trigger from an arbitrary external system (GitHub, Stripe, a monitoring alert, ...) whose
// payload doesn't look like {"message": "...", "conversationId": ...} — top-level JSON keys
// flatten into variables.<key>, the same mechanism RunForm.razor's formValues already uses for
// named form fields. No "message" required; a webhook sender doesn't have one to send. Each
// call is a fresh, throwaway run (never a resumed conversation) — same choice as a scheduled
// run, identified the same way via its ConversationId prefix.
runtimeApi.MapPost("/agents/{id:guid}/versions/{version:int}/webhook", async (
    Guid id, int version, HttpContext http,
    IAgentRepository agents, IProviderRepository providers, AgentService service,
    WorkflowRunner runner, CancellationToken ct) =>
{
    var apiKey = http.Request.Headers["X-Agent-Api-Key"].FirstOrDefault();
    var (agent, published, provider, error) = await ResolveRuntimeAsync(id, version, apiKey, agents, providers, service, ct);
    if (error is not null) return error;

    var values = new Dictionary<string, string>();
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync(ct);
    if (!string.IsNullOrWhiteSpace(body))
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(new { error = $"Webhook body is not valid JSON: {ex.Message}" });
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject())
                    values[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
        }
    }

    var conversation = new ConversationState
    {
        ConversationId = $"webhook-{Guid.NewGuid():N}",
        AgentId = agent!.Id,
        AgentVersion = published!.Version
    };

    var reply = new StringBuilder();
    await foreach (var chunk in runner.RunAsync(agent, published, provider!, conversation, "", ct, formValues: values))
        reply.Append(chunk);

    return Results.Ok(new ConversationResponse(conversation.ConversationId, reply.ToString(), runner.LastExecutionId ?? ""));
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Exposes the top-level Program for WebApplicationFactory&lt;Program&gt; in tests.</summary>
public partial class Program;

/// <summary>Rate limiting for the anonymous /auth/login endpoint — no lockout mechanism existed
/// before this (see DEPLOYMENT.md's security note). Partitioned per client IP
/// so one abusive client can't exhaust attempts for everyone; falls back to the per-request
/// TraceIdentifier when there's no real IP (e.g. an in-memory test server), so unrelated
/// requests never share a single bucket just because the transport can't report an address.</summary>
public static class AuthRateLimiting
{
    public const string PolicyName = "auth-login";
    public const int PermitLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.TraceIdentifier;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            QueueLimit = 0
        });
    }
}
