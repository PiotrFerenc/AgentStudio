using System.Security.Claims;
using System.Text;
using AgentStudio.Application;
using AgentStudio.Contracts;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using AgentStudio.Web.Components;
using AgentStudio.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAgentStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<StudioApiClient>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
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
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// ============================
// Auth (cookie-based, phase 2). Login/setup are plain form posts so Set-Cookie lands on a
// real HTTP response — not routed through a Blazor Interactive Server circuit.
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
});

auth.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

auth.MapPost("/setup", async (HttpContext http, UserService users, CancellationToken ct) =>
{
    if (await users.AnyUsersAsync(ct))
        return Results.Redirect("/login");

    var form = await http.Request.ReadFormAsync(ct);
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    if (string.IsNullOrWhiteSpace(username) || password.Length < 8)
        return Results.Redirect("/setup?error=1");

    await users.CreateAsync(username, password, UserRole.Admin, ct);
    return Results.Redirect("/login");
});

// ============================
// Studio management API (cookie-authenticated — phase 2)
// ============================

var api = app.MapGroup("/api").DisableAntiforgery().RequireAuthorization();

api.MapGet("/providers", async (IProviderRepository repo, CancellationToken ct) =>
    Results.Ok((await repo.ListAsync(ct)).Select(p => new ProviderDto(p.Id, p.Name, p.BaseUrl, p.DefaultModel, p.ApiKey is not null, p.EmbeddingModel))));

api.MapPost("/providers", async (CreateProviderRequest req, IProviderRepository repo, CancellationToken ct) =>
{
    var provider = new ModelProviderConfig { Name = req.Name, BaseUrl = req.BaseUrl, DefaultModel = req.DefaultModel, ApiKey = req.ApiKey, EmbeddingModel = req.EmbeddingModel };
    await repo.AddAsync(provider, ct);
    await repo.SaveChangesAsync(ct);
    return Results.Created($"/api/providers/{provider.Id}", new ProviderDto(provider.Id, provider.Name, provider.BaseUrl, provider.DefaultModel, provider.ApiKey is not null, provider.EmbeddingModel));
});

api.MapGet("/agents", async (IAgentRepository repo, CancellationToken ct) =>
    Results.Ok((await repo.ListAsync(ct)).Select(a => new AgentDto(a.Id, a.Name, a.Description, a.SystemInstructions, a.ModelProviderName, a.ModelName, a.CreatedAt))));

api.MapPost("/agents", async (CreateAgentRequest req, AgentService service, CancellationToken ct) =>
{
    var (agent, rawKey) = await service.CreateAsync(req, ct);
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

api.MapPut("/agents/{id:guid}/draft", async (Guid id, UpdateDraftRequest req, AgentService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.UpdateDraftAsync(id, req.Graph, req.MaxSteps, req.FormFields, ct));
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

api.MapPost("/agents/{id:guid}/publish", async (Guid id, AgentService service, CancellationToken ct) =>
{
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

api.MapPost("/agents/{id:guid}/regenerate-key", async (Guid id, AgentService service, CancellationToken ct) =>
{
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

api.MapPost("/agents/{id:guid}/versions/{version:int}/unpublish", async (Guid id, int version, AgentService service, CancellationToken ct) =>
{
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

api.MapPost("/agents/{id:guid}/versions/{version:int}/republish", async (Guid id, int version, AgentService service, CancellationToken ct) =>
{
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Exposes the top-level Program for WebApplicationFactory&lt;Program&gt; in tests.</summary>
public partial class Program;
