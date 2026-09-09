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
- `DatabaseQueryNode` always binds `Parameters` as real `NpgsqlParameter` values — `Query` itself is never template-expanded, only the named parameters are (SQL injection guard, same rigor as the HTTP tool's SSRF guard below). Every parameter is bound with `NpgsqlDbType.Unknown`, not a typed value — `Parameters` are always plain-text templates, and a typed `Text` parameter fails Postgres's implicit-cast rules against a non-text column (e.g. `WHERE customer_id=@id` against an `integer` column throws `42883: operator does not exist: integer = text`). `Unknown` makes Postgres infer the type from context, same as a literal in a plain-text query. Result shape is always `{"rows":[...],"rowCount":N,"truncated":bool}` — to branch on row count, follow it with a `jsonParse` node (`Path: rowCount`) into a `condition` node, there's no separate "row count" output.
- Optional `formValues` param on `RunAsync` merges into `conversation.Variables` *after* `variables["input"] = userMessage` — this is how the `/run/{agentId}/{version}` form page feeds named fields into `{variables.name}` templates. Applied last so a form run (which always passes `userMessage=""`) lets a field literally named `input` win and populate `{input}` too, instead of being silently wiped to empty.

### jsonb columns on entities

Several entity properties follow the same pattern: a `string XyzJson` column plus a `[NotMapped]` computed property that serializes on set / deserializes on get (see `AgentVersion.Graph`/`GraphJson` and `.FormFields`/`FormFieldsJson`). Two things to remember when adding one:
- If the property is a mutable collection that gets edited *in place* on a tracked entity (not replaced wholesale), configure a `JsonValueComparer<T>` for it in `AgentStudioDbContext.OnModelCreating` — EF's default reference-equality change tracking otherwise misses the mutation and silently skips the `UPDATE`.
- The getter should tolerate a non-array/malformed stored value rather than throwing — an EF-generated migration `defaultValue` can get coerced by Postgres into something that doesn't deserialize as expected (e.g. `defaultValue: ""` on a `jsonb` column became a stored `{}`, not `[]`, and crashed every page load for pre-migration rows until the getter was made defensive).

`DatabaseConnectionConfig` (phase 3, `databaseQuery` node) is bound from the `DatabaseConnections` config array (`appsettings.json`), not a DB table — `IDatabaseConnectionProvider`/`DatabaseConnectionProvider` is a thin synchronous `IOptions<List<...>>` read, no repository/CRUD. `Provider` defaults to `"postgres"` (the only one implemented); any other value is rejected at query time with a clear error rather than mishandled, since the field exists for a future connector type without a config-shape break.

### LLM call timeout

`OpenAiCompatibleChatClientFactory`/`OpenAiCompatibleEmbeddingClientFactory` set `OpenAIClientOptions.NetworkTimeout` from `ModelProviderConfig.TimeoutSeconds` (default 120s, clamped to [1, 600]). Without this, `System.ClientModel`'s own default (100s) silently cancels any `prompt`/`documentSearch` node whose model takes longer than 100s to respond — confirmed live with a mock server that delayed its response 110s: the call failed with `NetworkTimeout` at exactly 100.07s before this fix, succeeded at 110.29s after.

### Model providers are entirely appsettings.json config

`ModelProviderConfig` has **no database table** — `ProviderRepository` (`AgentStudio.Infrastructure`) is a thin `IOptions<List<ModelProviderConfig>>` reader over `appsettings.json`'s `"ModelProviders"` array, same shape as `DatabaseConnectionProvider`. `/providers` is read-only (no add/edit/delete — edit the file and restart). Each provider gets a deterministic `Id` (`DeterministicGuid.From($"provider:{name}")`, `AgentStudio.Domain/DeterministicGuid.cs` — SHA-256 of the seed string, first 16 bytes) since appsettings has nowhere to persist a random one. `ModelProviderConfig.Headers` (`Dictionary<string,string>`) are extra HTTP headers sent with every request to that provider — e.g. a gateway auth header some OpenAI-compatible proxy expects — applied via a `PipelinePolicy` (`ProviderClientOptions.Build`, `AgentStudio.Infrastructure`) that both `OpenAiCompatibleChatClientFactory` and `OpenAiCompatibleEmbeddingClientFactory` call instead of building `OpenAIClientOptions` inline (also where `TimeoutSeconds` above gets wired in — one place, not duplicated per factory). `ApiKey` is plaintext in the config file — no `SecretProtector`/encryption-at-rest layer exists any more, since there's no database column left to encrypt; secure the file itself (permissions, environment-variable overrides) the same way `Secrets:EncryptionKey` used to have to be protected.

### Form builder extensions

`AgentStudio.Domain/FormFieldValidator.cs` is a pure/static class (no DI, same spirit as
`ConditionEvaluator`) used by both the runtime `/run` page and the studio's live form preview.
Two-method contract: `IsVisible(FormField, IReadOnlyDictionary<string,string> values)` decides
whether a field with `VisibleWhenField`/`VisibleWhenEquals` set should currently be shown, and
`Validate(IReadOnlyList<FormField>, values)` returns one error message per invalid *visible*
field — a hidden field is never validated, even if `Required` and missing, since a value the
user was never shown can't reasonably be required.

`AgentVersion` gained `FormResultMode` ("inline"/"redirect"/"webhook"), `FormResultTarget`
(URL, supports `{result}`/`{conversationId}`/`{executionId}` placeholders) and
`FormResultMarkdown` (bool). Same "jsonb columns on entities" copy lesson above applies here:
these three needed (and got) the same explicit-copy treatment as `MaxSteps`/`FormFields` in
`AgentService.PublishAsync`/`RepublishAsync` — a new `AgentVersion` field that isn't copied in
both of those methods silently reverts to its default on every publish/republish, not just on
first creation.

`FormResultMarkdown` renders with a deliberately minimal, hand-rolled regex substitution
(bold/italic/code/links/headers/line breaks) over HTML-escaped input — not a real CommonMark
parser, no new NuGet dependency. It's a safe subset, not a compliant Markdown renderer.

### Custom integrators (phase 4)

`IntegratorNode { IntegratorName, Config, ResultVariable }` calls a registered `IIntegrator` by name — the same named-lookup shape as `DatabaseQueryNode`/`ConnectionName`. Writing a new integrator (GitLab/Jira are the reference implementations, in `AgentStudio.Infrastructure/Integrators/`) means implementing `IIntegrator` and adding one `services.AddTransient<IIntegrator, YourClass>()` line in `DependencyInjection.cs` — a code change + rebuild/redeploy, deliberately not a runtime plugin system. `WorkflowRunner` gets `IEnumerable<IIntegrator>` and does a plain `.FirstOrDefault(i => i.Name == ...)` lookup, no separate registry. `Config` values are template-expanded (`{input}`/`{variables.x}`) before reaching the integrator, same rigor as `DatabaseQueryNode.Parameters`. Per-integrator secrets (base URL, API token) live in `appsettings.json` under `Integrators:<Name>`, bound via `IOptions<TOptions>` — same split as `DatabaseConnections`.

New integrators should send their HTTP call through `IntegratorHttp.SendAsync` (same folder) rather than calling `HttpClient.SendAsync` directly — it caps the response body at 1MB, same reasoning as `SecureHttpExecutor`'s cap on `HttpNode`, and centralizes the send/status-check/error-message pattern so a third integrator doesn't reintroduce an unbounded read by copy-paste.

### Auth and rate limiting

Cookie auth (`Microsoft.AspNetCore.Authentication.Cookies`) with `PasswordHasher<T>`, not full ASP.NET Core Identity. Roles: Admin/Editor. **No database table, no `/setup` bootstrap** — user accounts are entirely `appsettings.json`'s `"Users"` array, same as `ModelProviders`. `UserRepository` (`AgentStudio.Infrastructure`) is a thin `IOptions<List<UserConfig>>` reader; `/users` is read-only (no add/edit/delete — edit the file and restart). Each entry (`AgentStudio.Domain/User.cs`'s `UserConfig`) sets either `Password` (plaintext, dev convenience — compared directly in `UserService.VerifyPasswordAsync` via `CryptographicOperations.FixedTimeEquals`, never hashed or stored) or `PasswordHash` (a real `PasswordHasher<User>` hash, for production). Deterministic `Id` (`DeterministicGuid.From($"user:{username}")`) matters at runtime here, unlike for providers: `Agent.OwnerId`/`AgentCollaborator.UserId` are FKs into user Ids, so a config account's Id has to stay stable across restarts for `AgentAccess.CanEdit` to keep recognizing it as the same owner/collaborator. If `appsettings.json` has zero `Users` entries, `/login` shows an inline "no users configured" message rather than a bootstrap flow — there's no other way to create the first account.

The studio UI and `/api/...` require a logged-in session. The runtime agent endpoints (`/api/agents/{id}/versions/{v}/conversations`, `/stream`, plus `/chat/*`, `/run/*`, `/widget/*`) are intentionally anonymous, protected only by a per-agent `X-Agent-Api-Key` header (SHA-256 hash, shown once).

`/auth/login` is rate-limited (`AuthRateLimiting` in `Program.cs`, ASP.NET Core's built-in `RateLimiter`, partitioned by client IP, 5 req/min). Note this is IP-based, not account lockout, and needs `UseForwardedHeaders` (not currently wired) to work correctly behind a reverse proxy — otherwise all clients behind the proxy share one budget.

`/api` and `/auth` are both excluded from `UseStatusCodePagesWithReExecute` — re-executing their error responses against the Razor `/not-found` page turns clean status codes (401/404/429) into misleading ones (400/404) because that page's POST handling expects antiforgery-validated form data, not a JSON body or a rate-limiter rejection.

### AgentDetail page tabs

`AgentDetail.razor` grew to ~8 stacked `<div class="panel">` blocks (settings, share, graph, docs, form+preview, test chat, logs) as features accumulated across phases — became unreadable as one long scroll. Restructured into a plain `_activeTab` string field + `@if (_activeTab == "...")` wrapping each existing panel block (no inner logic touched, purely a visibility gate) — tabs: `build` (default, graph+node properties), `overview` (name/status/publish/max steps/schedule/env vars/links/versions/diff/share), `form` (form builder+preview), `chat` (test chat), `docs`, `logs`. Hand-rolled tab strip (`.agent-tabs`/`.agent-tab-btn` in the page's own `<style>` block), no new component/library — same "smallest thing" choice as everywhere else in this project. Adding a new panel to this page: put it behind an existing tab if it fits thematically, or add a new `_activeTab` value + tab button rather than appending another always-visible panel to the bottom.

### Graph editor

`AgentStudio.Web/Components/GraphEditor.razor` is a hand-rolled SVG canvas (pan/zoom via CSS `transform`, mouse-event-based node dragging and edge linking) — no diagram/charting library in this project, by design (see `Analytics.razor`'s hand-drawn SVG bar charts for the same pattern applied elsewhere). `_canvasLeft`/`_canvasTop` (from a JS interop `getBoundingClientRect()` call) are re-measured at the start of every drag/link/pan gesture, not just once on first render, since the page can scroll between renders.

`AgentStudio.Web/Components/NodePropertiesEditor.razor`'s multi-value fields (`databaseQuery` Parameters, `integrator` Config) are edited as one `name=value`-per-line textarea that round-trips through a `Dictionary<string,string>` on every write — the getter reconstructs the textarea text from the dict. These two textareas bind on `onchange` (blur), not `oninput`: reparsing on every keystroke drops any line that doesn't yet contain `=` (i.e. while the user is still typing the parameter name), which snapped the textarea back and erased what was just typed.

### AI-generated graph fragments

`GraphEditor.razor`'s "Generate with AI" bar turns a plain-language description into a graph fragment, inserted the same way "Insert component" inserts a saved `GraphComponent` — fresh ids via `GraphComponentInserter.Clone`, offset so it never overlaps existing nodes. Never replaces the current graph (insert-only, by design — a bad generation costs a manual delete, not a lost draft). `AgentStudio.Application/GraphGenerationService.cs` does the work: builds a system prompt entirely from `NodeCatalog` (one loop over every entry's Type/Summary/Params — a new node type with a `NodeCatalog` entry becomes generatable automatically, nothing else to touch), calls the agent's own configured provider/model (no separate model picker), and expects `WorkflowGraphDto`-shaped JSON back (not `AgentStudioJson.Options`' Domain discriminated-union format — the flat `Props` dict is simpler for a model to produce correctly). The response is round-tripped through `GraphMapper.ToDomain` + `WorkflowValidator.Validate` before anything reaches the caller — a hallucinated node type or a structurally broken graph (no `Start`, a `Condition` missing its `false` edge, ...) comes back as a clear error message, never lands on the canvas. `GraphGenerationServiceTests` exercises this without a real LLM call via a canned-response fake `IChatClient`, same fake pattern `WorkflowTests.cs` already uses.

### Starter agent templates

`AgentStudio.Contracts/AgentTemplateCatalog.cs` is a small, fixed, code-defined set of complete starter agents (`AgentTemplate { Id, Name, Description, SystemInstructions, Graph, FormFields }`) — RAG Q&A, Webhook → Integrator, Formularz z walidacją — not a database table, same "curated and shipped with the app" spirit as `NodeCatalog`. Different from `GraphComponent` (a fragment pasted into an *existing* graph): a template is what a brand-new agent's draft starts as. Home.razor's "New agent" modal picks one (or "Blank", today's `AgentService.DefaultGraph()`); `AgentService.CreateAsync`'s optional `AgentTemplate? template` parameter seeds the new draft's `Graph`/`FormFields` from it and only fills `SystemInstructions` from the template when the create request left that field blank (a caller that already wrote instructions keeps them). Deliberately not exposed on the public REST `/api/agents` — it's a studio-UI convenience (`StudioApiClient.CreateAgentAsync`'s `templateId` param resolves via `AgentTemplateCatalog.Get`), not a wire contract. `AgentTemplateCatalogTests` runs every template's `Graph` through `WorkflowValidator` — same catalog-integrity discipline as `NodeCatalogTests`.

### Provider overrides — per agent and per node

`Agent.ModelProviderName`/`ModelName` were read-only once set at creation until `AgentService.UpdateProviderAsync` (Overview tab's "Model provider" field) — the agent's own default for every `prompt`/`documentSearch` node that doesn't set its own override.

`PromptNode` additionally carries `ProviderName`/`ModelName` and `DocumentSearchNode` carries `ProviderName` (both default `""` = inherit the agent's own) — set either to call a different provider (and, for `prompt`, a different model on it) for just that one node, without creating a separate agent. `WorkflowRunner.ResolveProviderAsync` is the single place that resolves an override name via `IProviderRepository.GetByNameAsync`, used identically by both node cases; an override naming an unconfigured provider fails the same way every other node failure does — caught by `ExecuteAsync`'s outer try/catch, surfaced as an `[error]` chunk in the output (WorkflowRunner never throws out of `RunAsync` for a node-level failure, by design), never a thrown exception at the caller. `subAgent` remains the other way to get a different provider mid-graph (it always uses the *target* agent's own provider, not the caller's) — these overrides are for "just swap the model for one step," not for delegating to a whole separate agent.

### Workflow node types

`start`, `prompt`, `message`, `condition`, `http`, `variable`, `documentSearch`, `subAgent`, `databaseQuery`, `jsonParse`, `expression`, `collectionGet`, `collectionSet`, `integrator`, `parallel`, `join`, `end`. Placeholders in templates: `{input}`, `{variables.name}`. Condition operators: `Equals`, `NotEquals`, `Contains`, `StartsWith`, `EndsWith`, `>`, `<`, `>=`, `<=`.

`AgentStudio.Contracts/NodeCatalog.cs` is the single source of truth for what each node type does and what each of its parameters means — one `NodeInfo { Type, Title, Summary, Params }` per type, used both by `GraphEditor`'s palette (`title` tooltip on each "Add" button) and `NodePropertiesEditor`'s per-node header (Title+Summary) and per-field hints (a small `FieldHint` component under each `<label>`, `AgentStudio.Web/Components/FieldHint.razor`). `NodeCatalogTests` reflects over every `WorkflowNode` subclass and fails if one has no catalog entry (or an empty Summary/param description) — same "can't add a node type without wiring it up" discipline as the JSON converter and `GraphMapper` cases above: add the `NodeInfo` in the same change as the new node type, not after.

`jsonParse` extracts one value from a JSON blob by a small dot/bracket path (`data.items[0].name`) — `AgentStudio.Domain/JsonPathExtractor.cs`, a hand-rolled walker over `System.Text.Json` (not a full JSONPath implementation — one value out of a known shape is the whole use case). `Input` is template-expanded, `Path` is not (it's a fixed path into a known response shape, not runtime-controlled). Throws a clear `InvalidOperationException` naming the bad property/index/JSON rather than returning silently empty — same fail-clearly contract as `DatabaseQueryNode`/`HttpNode`.

### Per-agent access control (phase 14)

`Agent.OwnerId`/`OwnerUsername` (set at creation from the logged-in user's claims — `Program.cs`'s `POST /api/agents` reads `ClaimTypes.NameIdentifier`/`ClaimTypes.Name`) plus `Agent.Collaborators` (`AgentCollaborator { AgentId, UserId, Username }`, cascade-deleted with the agent) answer "can this specific user edit this specific agent" — layered *on top of* the existing global Admin/Editor roles, not replacing them: `AgentAccess.CanEdit(Agent, User)` (pure static, `AgentStudio.Domain`) is `Admin || Owner || Collaborator`, nothing else. A single permission level (can-edit, full stop) — no separate Viewer tier, since the existing global roles already gate "can this user edit agents at all." `AgentRepository.GetAsync`/`ListAsync` **must** `.Include(a => a.Collaborators)` — `CanEdit` checks an empty-vs-missing list and a repository that forgets the Include makes every non-owner check silently deny, since an un-hydrated nav property is an empty `List<T>` by EF convention, not null (so the bug is silent, not a crash — the exact kind of gotcha this file's other "getter should tolerate/computed correctly" notes warn about).

Enforced in **two** places, deliberately: `AgentDetail.razor` gates the whole page (an access-denied panel instead of the editor) for UX, but `Program.cs`'s `CheckEditAccessAsync` helper — called from every mutating `/api/agents/{id}/...` endpoint (`draft` PUT, `publish`, `regenerate-key`, `unpublish`, `republish`) — is the actual security boundary: a client-side-only check is trivially bypassed by anyone with a valid session cookie hitting the REST API directly with curl, same reasoning as every other guard in this API (SSRF allowlist, SQL parameterization, etc.). Returns 403 (`Results.Json(..., statusCode: 403)`), not `Results.Forbid()` (that needs a challenge scheme configured for the *management* auth flow, which isn't the same as an anonymous-runtime 401 elsewhere in this file). `Home.razor`'s agent list is **not** filtered by ownership — every Editor/Admin still sees every agent (with an Owner column); only *editing* is gated. Sharing management (add/remove a collaborator) is further restricted to Owner-or-Admin — a plain collaborator can edit the agent but can't grant others access.

### Agent-level environment variables (phase 12)

`Agent.EnvironmentVariables` (`Dictionary<string,string>`, jsonb, same `ValueConverter`+`JsonValueComparer` as `ConversationState.Variables`) are named, admin-editable config values scoped to the *agent*, not a specific graph node — reachable in any template as `{variables.env.NAME}`. `WorkflowRunner.ExecuteAsync` seeds `variables["env." + name] = value` for each entry **before** `variables["input"]` and `formValues` are applied, so it's lowest precedence — a form field or webhook payload key can use the same name and win. `DebugNodeAsync` seeds them too (before `sampleVariables`, same precedence order), so a node under test sees the same env values a real run would. Despite the name, this isn't a dev/test/prod multi-deployment mechanism (the app doesn't have separate simultaneous deployments) — it's "named config values instead of hardcoding a value in a node," e.g. `{variables.env.API_URL}` instead of pasting the URL into an `HttpNode`.

### Triggers beyond chat/form (phase 11)

Two ways to run an agent without a live requester: `POST /api/agents/{id}/versions/{v}/webhook` (`Program.cs`, alongside `/conversations`/`/stream`) accepts an arbitrary JSON body — not the `{"message": ...}` shape — and flattens its top-level keys straight into `variables.<key>` via `RunAsync`'s `formValues` parameter (the same mechanism `RunForm.razor` already uses for named form fields), so an external system's own payload shape (GitHub, Stripe, a monitoring alert) doesn't need massaging into a chat request first. No `message`/`{input}` — nothing to send one. Malformed JSON is a clear 400, not a 500; an empty body just means no variables, not an error. Each call gets a fresh, unpersisted `ConversationState` (`webhook-{guid}` prefix, same non-persisted-but-logged choice as a form run).

`Agent` (not `AgentVersion`) carries `ScheduleEnabled`/`ScheduleIntervalMinutes`/`ScheduleInput`/`LastScheduledRunAt` — deliberately on the agent, not a specific version, so a recurring schedule always runs whatever is currently the latest *published* version rather than freezing to whatever version existed when the schedule was turned on (same latest-published lookup as `SubAgentNode`). `ScheduledRunner` (`AgentStudio.Infrastructure`, a `BackgroundService`, same shape as `ConversationRetentionService`) polls every 30s (`ScheduledRunnerOptions.PollInterval`) and fires any due agent sequentially within one pass — no per-agent concurrency, so a slow run can't overlap a later poll tick re-triggering the same agent, at the cost of poll-drift if many agents are due at once. `LastScheduledRunAt` updates only after `RunAsync` completes (success or an in-graph `[error]` — `WorkflowRunner` never throws for a node-level failure by design) — an exception reaching `ScheduledRunner` means something *outside* the graph broke (e.g. provider missing), and deliberately isn't recorded, so the next 30s poll retries promptly instead of waiting a full interval on a config problem. `AgentService.UpdateScheduleAsync` rejects `enabled=true` with no (or a non-positive) interval — the guard lives in the service, not just the runner, so a stray enabled-with-null-interval row can't sit in the DB implying some default cadence that doesn't exist. Single-process, no distributed lock: more than one `AgentStudio.Web` instance against the same DB double-fires every schedule (documented in DEPLOYMENT.md, not solved — `ponytail:` upgrade path is a DB-level claim on `LastScheduledRunAt`).

### Graph components (phase 10)

Copy-paste reuse, not live binding: `GraphEditor.razor` lets the user Ctrl/Cmd+click to toggle nodes into `_componentSelection` (a separate `HashSet<string>`, independent of the normal single `SelectedNode` used by the properties panel; `start` can't be added), then "Save as component" bundles those nodes plus only the edges where *both* endpoints are also selected into a `WorkflowGraphDto` and hands it to the parent via `ComponentSaveRequested` — the editor has no direct DB access, same separation `GraphChanged` already uses for ordinary edits. `GraphComponent { Id, Name, Description, GraphJson, CreatedAt }` (table `GraphComponents`, global — not per-agent) stores `GraphJson` as an opaque string in Domain: Domain doesn't reference Contracts, so it can't know the `WorkflowGraphDto` shape it's holding — the typed (de)serialization happens in `StudioApiClient` (Web), which references both. "Insert component" clones via `AgentStudio.Contracts/GraphComponentInserter.Clone` (pure/static, same spirit as `GraphDiff`): fresh ids from a caller-supplied generator (so two inserts of the same component, even into the same graph, never collide), positions normalized to the component's own bounding box (caller adds an offset), and nested `Dictionary<string,string>` props (`Parameters`/`Config`/`Headers`) deep-copied — without that, two clones would share the same dictionary instance and editing one node's `Parameters` would silently corrupt the other. A component can never contain `start`/`end` (structural, unique per graph) — nothing enforces this beyond the UI not letting `start` into the selection; an `end` node CAN currently be ctrl-clicked and saved (harmless on its own, but pasting it into a graph that already has an End node just adds a second dead-end node, not a validator error, since `WorkflowValidator` doesn't cap End nodes at one).

### Persistent collections (phase 9)

`IAgentCollectionStore` (`AgentCollectionEntry { AgentId, Key, Value, UpdatedAt }`, table `AgentCollectionEntries`, composite PK `(AgentId, Key)`) is a small per-agent key/value store that survives across runs, conversations, and published versions — unlike `ConversationState.Variables`, which resets on every new conversation. One row per key, deliberately not a single jsonb blob on `Agent` — concurrent runs of the same published agent (many chat users) writing to a whole-dictionary column would race on `SaveChanges` and silently drop whichever write lost; one row per key means concurrent writes to *different* keys never clobber each other (same-key concurrent writes still last-write-wins, but that's ordinary row-level DB semantics, not a lost-update-of-everything-else bug). `collectionSet { Key, Value }` and `collectionGet { Key, DefaultValue, ResultVariable }` are both template-expanded on `Key` (and `Value`/`DefaultValue`) via the normal `ExpandTemplate`, unlike `ExpressionNode`'s formula. An empty `Key` after expansion is a clear `InvalidOperationException`, not a silent write under `""`. Cascade-deletes with its owning `Agent` (not that agent deletion exists in the UI today, but the FK is there for when it does).

### Formula node (phase 8)

`ExpressionNode { Formula, ResultVariable }` computes one value with a small spreadsheet-like formula language — `AgentStudio.Domain/FormulaEvaluator.cs`, a hand-rolled recursive-descent parser (arithmetic `+ - * /`, comparisons `== != < > <= >=`, boolean `&& || !`, parens, functions `IF`/`CONCAT`/`LEN`/`UPPER`/`LOWER`/`TRIM`/`ROUND`/`ABS`). Zero NuGet dependency, same "smallest thing that solves the use case" choice as `JsonPathExtractor` not being full JSONPath. `{input}`/`{variables.x}` placeholders are resolved to **typed atomic tokens** during scanning (number if the stored text parses as one, bool for exactly `"true"`/`"false"`, string otherwise) — never text-substituted into the formula source the way `ExpandTemplate` works elsewhere — so a variable whose value happens to contain `)`/`,`/operators can never break the grammar or get re-parsed as formula syntax. `WorkflowRunner`'s `ExpressionNode` case calls `FormulaEvaluator.Evaluate` directly (no `ExpandTemplate` step) for exactly this reason. Fails clearly (unknown function, wrong arg count, division by zero, non-numeric value in arithmetic) rather than silently coercing — same contract as `JsonPathExtractor`/`DatabaseQueryNode`.

### Single-node debug (phase 6)

`WorkflowRunner.DebugNodeAsync(agent, provider, graph, nodeId, sampleVariables, ct)` runs exactly one node from a (possibly unsaved) graph against caller-supplied sample variables — backs the graph editor's "Test this node" panel (`NodePropertiesEditor.razor`, excluded for `start`/`parallel`/`join`). It builds a synthetic one-node `WorkflowGraph` (the target node, zero edges) and calls the same private `RunSegmentAsync` the real run uses, so every node type's actual logic runs unmodified — it just has nowhere to go afterward (`NextByEdge` finds no edge, the loop ends after one iteration). A node's own branching still shows up in `NodeDebugResult.Detail` (e.g. a condition's `true`/`false`), it just never continues to whatever that branch would normally reach. Nodes that call an LLM/HTTP/DB/integrator/sub-agent make a real call — that's the point, not a side effect to guard against. Never persisted: `_logWriter.Start`/`StartStep`/`CompleteStep` are in-memory only (see `ExecutionLogWriter`), and `CompleteAsync` — the only method that actually writes to the DB — is deliberately never called, so debug runs don't show up in `/analytics` or the agent's execution log. `StudioApiClient.DebugNodeAsync` is the Blazor-facing wrapper (resolves agent + provider, `GraphMapper.ToDomain`s the DTO graph the editor currently has open, including unsaved edits).

### Graph version diff (phase 6)

`AgentStudio.Contracts/GraphDiff.cs` — pure/static (same spirit as `ConditionEvaluator`/`FormFieldValidator`), compares two `WorkflowGraphDto`s: nodes matched by `Id` (added/removed/modified, with which prop keys changed — including `label`/`type`), edges matched by `Id` (added/removed/modified on source/target/branch). Operates on `WorkflowNodeDto.Props` rather than typed `WorkflowNode` subclasses, so one comparison works for every node type with no per-type case, same reasoning as `GraphMapper`'s generic `Props` dict existing in the first place. AgentDetail.razor's "Compare" selector offers every published version plus "Draft" (when one exists) — entirely client-side, no new endpoint, since Blazor Interactive Server already has the whole `Agent` (all versions, each with a lazily-deserialized `.Graph`) loaded in memory.

### Database result table (phase 6)

`AgentStudio.Domain/DatabaseResultTable.cs` recognizes a `DatabaseQueryNode` result's own JSON envelope (`{"rows":[...],"rowCount":N,"truncated":bool}` — the only producer of this exact shape is `DatabaseQueryExecutor`) and renders it as an HTML table instead of a JSON blob wherever an agent's output can be exactly that shape (e.g. an End node's `{variables.dbResult}` with no `jsonParse` in between): `RunForm.razor` (form result), `PublicChat.razor` (per-message, only for `assistant` messages — never on the still-streaming bubble, since it's the completed `_messages` entries that get checked), and the studio's own draft test-chat in `AgentDetail.razor`. Anything else — a normal chat reply, a `jsonParse`'d single value — doesn't match and falls through to the existing markdown/plain-text rendering unchanged. Shared CSS (`.chat-result-table*`) lives in `ChatLayout.razor` (public chat/run pages) and `MainLayout.razor` (studio) — two copies because the two surfaces don't share a layout, not because the rule differs.

### HTTP tool (SSRF guard)

`SecureHttpExecutor` blocks loopback/private-range/link-local hosts by default, caps responses at 1 MB, follows at most 3 redirects, and strips sensitive headers (`authorization`, `cookie`, `x-api-key`) from logs. Allow internal hosts via `"HttpTool": { "AllowedHosts": [...] }` in config.

## Testing conventions

- Domain/Application-level tests use EF Core InMemory directly (`new AgentStudioDbContext(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(name).Options)`), one unique DB name per test.
- `ApiEndpointTests.cs` drives the real Minimal API endpoints through `WebApplicationFactory<Program>`. Its `Factory.ConfigureWebHost` overrides `ConnectionStrings:AgentStudio` to `"InMemory"` via `ConfigureAppConfiguration` *before* `ConfigureServices` runs — this has to happen before `Program.cs`'s `AddAgentStudioInfrastructure` reads the config, otherwise both the Npgsql and InMemory providers get registered and DbContext resolution throws.
- `DatabaseQueryExecutorTests.cs` runs against a real Postgres (the same dev instance on `localhost:5433`) to prove parameterization actually defeats SQL injection — this one can't be faked with InMemory.
- `WebApplicationFactory`'s TestServer never sets `HttpContext.Connection.RemoteIpAddress` (in-memory transport, no real socket) — tests that need to exercise IP-partitioned behavior (e.g. `AuthRateLimitingTests`) build a `DefaultHttpContext` directly instead of going through HTTP.
