# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Build:
```bash
dotnet build
```

Run all tests (always use a timeout — see "Critical invariant" below for why):
```bash
timeout 90 dotnet test AgentStudio.Tests/AgentStudio.Tests.csproj
```

Run a single test:
```bash
dotnet test AgentStudio.Tests/AgentStudio.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"
```

Run the dev server (from `AgentStudio.Web/`, or `dotnet run --project AgentStudio.Web` from repo root):
```bash
cd AgentStudio.Web && dotnet run
```
Single app on `http://localhost:5251` — studio UI, `/api/...`, SSE, widget, public `/chat` and `/run` pages all served together, no separate API process.

**Requires Postgres.** `appsettings.Development.json` points at `Host=localhost;Port=5433;...` (port 5433, not 5432, to avoid colliding with another local Postgres). Start it:
```bash
docker run -d --name agentstudio-postgres \
  -e POSTGRES_DB=agentstudio -e POSTGRES_USER=agentstudio -e POSTGRES_PASSWORD=agentstudio \
  -p 5433:5432 -v agentstudio_pgdata:/var/lib/postgresql/data postgres:17
```
EF Core migrations apply automatically at app startup (`MigrateAsync`). `ConnectionStrings:AgentStudio` empty or the literal `"InMemory"` falls back to EF InMemory (used by CI and by `ApiEndpointTests`' `WebApplicationFactory`).

EF migrations (needs `dotnet-ef` on PATH):
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add <Name> --project AgentStudio.Infrastructure --startup-project AgentStudio.Web
dotnet ef database update --project AgentStudio.Infrastructure --startup-project AgentStudio.Web \
  --connection "Host=localhost;Port=5433;Database=agentstudio;Username=agentstudio;Password=agentstudio"
```
The design-time factory (`AgentStudioDbContextFactory`) uses a placeholder connection string with no port — `database update` needs `--connection` pointed at the real dev DB explicitly, or it tries port 5432.

## Architecture

Six-project layered solution (`AgentStudio.slnx`), dependencies flow one direction:

```
Domain → Application → Infrastructure ↘
       ↘ Contracts ─────────────────→ Web
```

- **`AgentStudio.Domain`** — entities (`Agent`, `AgentVersion`, `WorkflowGraph`/`WorkflowNode` subclasses, `ExecutionLog`, `ConversationState`, `User`), plus config POCOs like `DatabaseConnectionConfig`, `WorkflowValidator`, `ConditionEvaluator`, `CosineSimilarity`, `DocumentChunker`. No dependencies on anything else in the solution.
- **`AgentStudio.Application`** — `WorkflowRunner` (the execution engine), `AgentService`, `UserService`, and all the `I*` interfaces Infrastructure implements (`IAgentRepository`, `IChatClientFactory`, `IDatabaseQueryExecutor`, `IAnalyticsRepository`, etc.).
- **`AgentStudio.Infrastructure`** — EF Core (`AgentStudioDbContext`, migrations), repository implementations, `SecureHttpExecutor` (SSRF-guarded HTTP tool), chat/embedding client factories (OpenAI-compatible), `NpgsqlDatabaseQueryExecutor`.
- **`AgentStudio.Contracts`** — DTOs for the Blazor-facing wire format (`WorkflowGraphDto`, `WorkflowNodeDto`, etc.) and `GraphMapper`, which translates between `Domain.WorkflowGraph` and the DTO shape.
- **`AgentStudio.Web`** — Blazor Web App (Interactive Server) + ASP.NET Core Minimal API in one process. `Program.cs` has both the `/auth/*` and `/api/*` minimal-API endpoints and the Razor Components bootstrapping. `Services/StudioApiClient.cs` is the facade Blazor pages inject instead of talking to repositories directly.

### WorkflowRunner — the execution engine

`AgentStudio.Application/WorkflowRunner.cs` executes a `WorkflowGraph` from its Start node, streaming output through a `Channel<string>`. `RunAsync` wraps `ExecuteAsync` (which does the real work) and yields chunks as they arrive.

**Critical invariant**: everything in `ExecuteAsync` — including `version.Graph` deserialization — must run inside the outer `try/finally`, because `output.Complete()` has to fire no matter what fails. If it doesn't, `RunAsync`'s `await foreach` over the channel reader hangs forever (a real deadlock, previously hung `dotnet test` for 120+ seconds). This is why `timeout` is mandatory when running tests.

The most common way to trip this: adding a new `WorkflowNode` subclass without adding its case to `WorkflowNodeConverter.Read` (`AgentStudio.Domain/AgentStudioJson.cs`) — the resulting `JsonException` during graph deserialization used to land outside the try/finally. **Whenever you add a new node type, add its JSON converter case and its `GraphMapper.ToDomain`/`ToDto` case in the same change**, before writing anything else.

Other runner details worth knowing:
- Loops are cycles in the graph, guarded by `AgentVersion.MaxSteps` (a per-run step counter, not a distinct "loop node").
- `ParallelNode`/`JoinNode` fan out branches concurrently; each branch buffers its output via a `ChunkSink` delegate instead of writing straight to the channel, so concurrent branches' streamed text never interleaves — the `JoinNode` flushes each branch's buffer in declared edge order once all branches finish.
- `SubAgentNode` recursively calls `RunAsync` on the same `WorkflowRunner` instance for a different (published) agent, with a throwaway, unpersisted `ConversationState`. `callDepth` (default 0, capped at `MaxSubAgentDepth = 5`) guards against agent-calls-agent cycles, since the single-graph `WorkflowValidator` can't see across agents. `LastExecutionId` is only set when `callDepth == 0`, since nested calls reuse the same runner instance.
- `DatabaseQueryNode` always binds `Parameters` as real `NpgsqlParameter` values — `Query` itself is never template-expanded, only the named parameters are (SQL injection guard, same rigor as the HTTP tool's SSRF guard below).
- Optional `formValues` param on `RunAsync` merges into `conversation.Variables` before `variables["input"] = userMessage` — this is how the `/run/{agentId}/{version}` form page feeds named fields into `{variables.name}` templates.

### jsonb columns on entities

Several entity properties follow the same pattern: a `string XyzJson` column plus a `[NotMapped]` computed property that serializes on set / deserializes on get (see `AgentVersion.Graph`/`GraphJson` and `.FormFields`/`FormFieldsJson`). Two things to remember when adding one:
- If the property is a mutable collection that gets edited *in place* on a tracked entity (not replaced wholesale), configure a `JsonValueComparer<T>` for it in `AgentStudioDbContext.OnModelCreating` — EF's default reference-equality change tracking otherwise misses the mutation and silently skips the `UPDATE`.
- The getter should tolerate a non-array/malformed stored value rather than throwing — an EF-generated migration `defaultValue` can get coerced by Postgres into something that doesn't deserialize as expected (e.g. `defaultValue: ""` on a `jsonb` column became a stored `{}`, not `[]`, and crashed every page load for pre-migration rows until the getter was made defensive).

### Secrets at rest

`ModelProviderConfig.ApiKey` is encrypted (AES-256-GCM) via an EF `ValueConverter` in `AgentStudioDbContext` backed by `SecretProtector` (`AgentStudio.Infrastructure`). The key is process-global config (`Secrets:EncryptionKey`, base64), set once via `SecretProtector.Configure` in `AddAgentStudioInfrastructure` — deliberately a static holder, not a DI-scoped service, because EF caches the model (and its converters) once per process, and a converter that captured a per-request-resolved service would silently keep using whichever scope built the model first. No key configured = no-op (dev/CI default). `Unprotect` tolerates anything that isn't its own ciphertext format (legacy plaintext rows, or rows written before a key existed) and returns it as-is — same defensive-getter lesson as the jsonb columns above. `DatabaseConnectionConfig` (below) deliberately isn't in this system: it moved out of the database entirely into `appsettings.json`, so encrypting it here wouldn't apply.

`DatabaseConnectionConfig` (phase 3, `databaseQuery` node) is bound from the `DatabaseConnections` config array (`appsettings.json`), not a DB table — `IDatabaseConnectionProvider`/`DatabaseConnectionProvider` is a thin synchronous `IOptions<List<...>>` read, no repository/CRUD. `Provider` defaults to `"postgres"` (the only one implemented); any other value is rejected at query time with a clear error rather than mishandled, since the field exists for a future connector type without a config-shape break.

### LLM call timeout

`OpenAiCompatibleChatClientFactory`/`OpenAiCompatibleEmbeddingClientFactory` set `OpenAIClientOptions.NetworkTimeout` from `ModelProviderConfig.TimeoutSeconds` (default 120s, clamped to [1, 600]). Without this, `System.ClientModel`'s own default (100s) silently cancels any `prompt`/`documentSearch` node whose model takes longer than 100s to respond — confirmed live with a mock server that delayed its response 110s: the call failed with `NetworkTimeout` at exactly 100.07s before this fix, succeeded at 110.29s after. `TimeoutSeconds` isn't exposed in `/providers`' UI yet (create-only form) — changing it per-provider means editing the DB column directly.

### Custom integrators (phase 4)

`IntegratorNode { IntegratorName, Config, ResultVariable }` calls a registered `IIntegrator` by name — the same named-lookup shape as `DatabaseQueryNode`/`ConnectionName`. Writing a new integrator (GitLab/Jira are the reference implementations, in `AgentStudio.Infrastructure/Integrators/`) means implementing `IIntegrator` and adding one `services.AddTransient<IIntegrator, YourClass>()` line in `DependencyInjection.cs` — a code change + rebuild/redeploy, deliberately not a runtime plugin system. `WorkflowRunner` gets `IEnumerable<IIntegrator>` and does a plain `.FirstOrDefault(i => i.Name == ...)` lookup, no separate registry. `Config` values are template-expanded (`{input}`/`{variables.x}`) before reaching the integrator, same rigor as `DatabaseQueryNode.Parameters`. Per-integrator secrets (base URL, API token) live in `appsettings.json` under `Integrators:<Name>`, bound via `IOptions<TOptions>` — same split as `DatabaseConnections`.

New integrators should send their HTTP call through `IntegratorHttp.SendAsync` (same folder) rather than calling `HttpClient.SendAsync` directly — it caps the response body at 1MB, same reasoning as `SecureHttpExecutor`'s cap on `HttpNode`, and centralizes the send/status-check/error-message pattern so a third integrator doesn't reintroduce an unbounded read by copy-paste.

### Auth and rate limiting

Cookie auth (`Microsoft.AspNetCore.Authentication.Cookies`) with `PasswordHasher<T>`, not full ASP.NET Core Identity. Roles: Admin/Editor. First run with zero accounts redirects `/` → `/login` → `/setup` to bootstrap the first Admin.

The studio UI and `/api/...` require a logged-in session. The runtime agent endpoints (`/api/agents/{id}/versions/{v}/conversations`, `/stream`, plus `/chat/*`, `/run/*`, `/widget/*`) are intentionally anonymous, protected only by a per-agent `X-Agent-Api-Key` header (SHA-256 hash, shown once).

`/auth/login` and `/auth/setup` are rate-limited (`AuthRateLimiting` in `Program.cs`, ASP.NET Core's built-in `RateLimiter`, partitioned by client IP, 5 req/min). Note this is IP-based, not account lockout, and needs `UseForwardedHeaders` (not currently wired) to work correctly behind a reverse proxy — otherwise all clients behind the proxy share one budget.

`/api` and `/auth` are both excluded from `UseStatusCodePagesWithReExecute` — re-executing their error responses against the Razor `/not-found` page turns clean status codes (401/404/429) into misleading ones (400/404) because that page's POST handling expects antiforgery-validated form data, not a JSON body or a rate-limiter rejection.

### Graph editor

`AgentStudio.Web/Components/GraphEditor.razor` is a hand-rolled SVG canvas (pan/zoom via CSS `transform`, mouse-event-based node dragging and edge linking) — no diagram/charting library in this project, by design (see `Analytics.razor`'s hand-drawn SVG bar charts for the same pattern applied elsewhere). `_canvasLeft`/`_canvasTop` (from a JS interop `getBoundingClientRect()` call) are re-measured at the start of every drag/link/pan gesture, not just once on first render, since the page can scroll between renders.

### Workflow node types

`start`, `prompt`, `message`, `condition`, `http`, `variable`, `documentSearch`, `subAgent`, `databaseQuery`, `parallel`, `join`, `end`. Placeholders in templates: `{input}`, `{variables.name}`. Condition operators: `Equals`, `NotEquals`, `Contains`, `StartsWith`, `EndsWith`, `>`, `<`, `>=`, `<=`.

### HTTP tool (SSRF guard)

`SecureHttpExecutor` blocks loopback/private-range/link-local hosts by default, caps responses at 1 MB, follows at most 3 redirects, and strips sensitive headers (`authorization`, `cookie`, `x-api-key`) from logs. Allow internal hosts via `"HttpTool": { "AllowedHosts": [...] }` in config.

## Testing conventions

- Domain/Application-level tests use EF Core InMemory directly (`new AgentStudioDbContext(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(name).Options)`), one unique DB name per test.
- `ApiEndpointTests.cs` drives the real Minimal API endpoints through `WebApplicationFactory<Program>`. Its `Factory.ConfigureWebHost` overrides `ConnectionStrings:AgentStudio` to `"InMemory"` via `ConfigureAppConfiguration` *before* `ConfigureServices` runs — this has to happen before `Program.cs`'s `AddAgentStudioInfrastructure` reads the config, otherwise both the Npgsql and InMemory providers get registered and DbContext resolution throws.
- `DatabaseQueryExecutorTests.cs` runs against a real Postgres (the same dev instance on `localhost:5433`) to prove parameterization actually defeats SQL injection — this one can't be faked with InMemory.
- `WebApplicationFactory`'s TestServer never sets `HttpContext.Connection.RemoteIpAddress` (in-memory transport, no real socket) — tests that need to exercise IP-partitioned behavior (e.g. `AuthRateLimitingTests`) build a `DefaultHttpContext` directly instead of going through HTTP.
