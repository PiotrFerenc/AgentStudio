# AgentStudio — Deployment (IIS + SQLite)

Production topology: a single ASP.NET Core app (`AgentStudio.Web`) hosted in-process by IIS,
backed by SQLite (a single file — app storage: agents, versions, conversations, documents,
execution logs). Postgres is optional and only needed if agents use the `databaseQuery` workflow
node (queries against external databases, unrelated to the app's own storage). The studio UI, the `/api/...` REST endpoints, the public chat page
(`/chat/{agentId}/{version}`), the public form page (`/run/{agentId}/{version}`, phase 3 —
a one-shot alternative to chat) and the embeddable widget (`/widget/agentstudio.js`) are all
served by this one app — there is no separate API process.

> **Security note — studio auth (phase 2).** The studio UI and the management `/api/...`
> endpoints require a logged-in account (cookie auth, Admin/Editor roles). Accounts are entirely
> defined in `appsettings.json`'s `"Users"` array (no database table, no `/setup` bootstrap —
> see "Model providers and user accounts in appsettings.json" below) — `/login` shows an inline
> message if none are configured. `/users` is a read-only list. The runtime agent endpoints
> (`/api/agents/{id}/versions/{v}/conversations` and `/stream`) stay anonymous, protected only
> by the per-agent `X-Agent-Api-Key` header — those (plus `/chat/*`, `/run/*` and `/widget/*`)
> are the endpoints safe to expose publicly; still bind the rest to an internal interface or a
> reverse proxy with IP restrictions.
>
> `/auth/login` is rate-limited to 5 requests/minute per client IP (returns
> `429`, `AuthRateLimiting` in `Program.cs`, ASP.NET Core's built-in rate limiter — no new
> dependency). **This is IP-based, not account-lockout** — it throttles one client hammering the
> form, not credential stuffing spread across many IPs. It also relies on
> `HttpContext.Connection.RemoteIpAddress` being the real client address: **behind a reverse
> proxy (ARR/nginx) this middleware isn't configured to trust `X-Forwarded-For`**, so every
> request arrives as the proxy's own IP and the 5/minute budget is shared by *all* users behind
> that proxy, not per-visitor. If you put a reverse proxy in front and see legitimate users
> getting `429`, wire up `UseForwardedHeaders` (trusting only your proxy's address) before this
> limiter — not done here since it wasn't needed for the direct-connection (no proxy) case this
> was written against.

## 1. Publish the app

From the repo root on a machine with the .NET 10 SDK:

```bash
dotnet publish AgentStudio.Web -c Release -o ./publish
```

Copy `./publish` to the IIS server (e.g. `C:\inetpub\agentstudio`). The publish is
framework-dependent by default; install the **ASP.NET Core 10 Hosting Bundle** on the server
(includes AspNetCoreModuleV2 and registers it with IIS). Alternatively publish self-contained:

```bash
dotnet publish AgentStudio.Web -c Release -r win-x64 --self-contained -o ./publish
```

## 2. Storage

### App storage — SQLite (required)

No server to install. Create `appsettings.Production.json` next to `AgentStudio.Web.dll` pointing
at a file — an absolute path is safest under IIS, since the app's working directory isn't always
what you'd expect:

```json
{
  "ConnectionStrings": {
    "AgentStudio": "Data Source=C:\\inetpub\\agentstudio\\data\\agentstudio.db"
  }
}
```

EF Core migrations run automatically at startup (`MigrateAsync`) and create the file if it
doesn't exist yet — just make sure the app pool identity has write access to that directory. If
`ConnectionStrings:AgentStudio` is empty or the literal `"InMemory"`, the app falls back to EF
InMemory (data lost on every restart) — fine for CI, not for production.

### Optional: Postgres for the `databaseQuery` node

Only needed if agents use the `databaseQuery` workflow node (phase 3) to query external
databases — unrelated to the app's own storage above.

```bash
docker compose up -d postgres
```

(repo includes `docker-compose.yml`; starts `postgres:17`, host port **5433** → container 5432,
remapped from the default 5432 because that's commonly already taken by another local Postgres on
a dev box — on a dedicated production host, either keep 5433 or edit `docker-compose.yml` back to
`"5432:5432"`. Requires the `docker compose` v2 plugin, not the ancient `docker-compose` v1
binary). Or install natively:

```sql
CREATE USER agentstudio WITH PASSWORD 'change-me';
CREATE DATABASE agentstudio OWNER agentstudio;
```

Then add a `DatabaseConnections` array to `appsettings.Production.json` — these are config, not
admin-panel data, so they're set once per deployment rather than through the UI:

```json
{
  "DatabaseConnections": [
    { "Name": "reporting", "Provider": "postgres", "ConnectionString": "Host=...;...", "ReadOnly": true }
  ]
}
```

`Provider` only supports `"postgres"` today; any other value fails clearly at query time rather
than being silently mishandled. `/database-connections` in the studio (Admin only) shows this
list read-only (name/provider/read-only flag, never the connection string) — there's no
add/delete there, only in config.

If agents use the `integrator` workflow node (phase 4) with the reference GitLab/Jira
integrators, add an `Integrators` section — same reasoning, deployment config rather than
admin-panel data:

```json
{
  "Integrators": {
    "GitLab": { "BaseUrl": "https://gitlab.example.com", "ApiToken": "glpat-..." },
    "Jira": { "BaseUrl": "https://your-domain.atlassian.net", "Email": "bot@example.com", "ApiToken": "..." }
  }
}
```

An integrator whose section is missing/empty throws a clear "not configured" error when a
workflow reaches its node, rather than the app crashing at startup — you only need the sections
for integrators an agent's graph actually uses. Writing a new integrator (any external system,
not just GitLab/Jira) means adding an `IIntegrator` implementation in
`AgentStudio.Infrastructure/Integrators/` and one `services.AddTransient<IIntegrator, ...>()`
line in `DependencyInjection.cs` — a code change + redeploy, not a runtime plugin: see
`PLAN.md`'s phase 4 section for the design rationale.

EF Core migrations run automatically at startup (`MigrateAsync`) — no manual `dotnet ef database update`
is needed, but the DB user needs DDL rights on first start. The migration is
`AgentStudio.Infrastructure/Migrations/*_Initial.cs` (Npgsql provider, `jsonb` columns). The
form-builder expansion (extended `FormField`, `AgentVersion.FormResultMode`/`FormResultTarget`/
`FormResultMarkdown`) added `AddFormFieldExtensionsAndResultBehavior` — applies automatically
the same way, nothing extra needed on deploy.

If `ConnectionStrings:AgentStudio` is empty or the literal `"InMemory"`, the app falls back to
EF InMemory (dev mode — data is lost on restart).

### Model providers and user accounts — entirely appsettings.json now

Both model providers and user accounts are config-only — **no database table for either**, and
no `/setup`/manual-add UI. `"ModelProviders"` (array of
`{ Name, BaseUrl, DefaultModel, ApiKey, EmbeddingModel, Headers }`) and `"Users"` (array of
`{ Username, Password, PasswordHash, Role }`, `Role` one of `"Admin"`/`"Editor"`) are the only
source for either. `/providers` and `/users` are read-only lists — add, remove, or change an
entry by editing `appsettings.json` and restarting.

**A `Users` entry's password reaches the file in the clear unless you set `PasswordHash`
instead of `Password`.** `Password` is a plaintext dev convenience, compared directly at login —
fine for a local/CI setup, not for anything that goes to a shared repo or a production host.
For production, hash it the same way the app does (`PasswordHasher<User>` — see
`AgentStudio.Application/UserService.cs`) and put the hash in `PasswordHash` instead:

```json
{ "Users": [ { "Username": "ops", "PasswordHash": "AQAAAAIAAYagAAAAE...", "Role": "Admin" } ] }
```

Same goes for a `ModelProviders` entry's `ApiKey` — it's plaintext in the file too; there's no
encryption-at-rest layer any more (no database column left to encrypt). Treat
`appsettings.Production.json` as a secret once it holds real credentials: restrict its file
permissions (`chmod 600`), keep it out of source control, and prefer environment variables
(`Users__0__PasswordHash=...`, `ModelProviders__0__ApiKey=...` — `__`-nesting, standard ASP.NET
Core config binding) over a committed file where the deployment pipeline allows it.

If `appsettings.json` has zero `Users` entries, `/login` shows an inline "no users configured"
message rather than any bootstrap flow — there's no other way to create the first account.

## 3. IIS site (in-process)

1. Install the ASP.NET Core 10 Hosting Bundle, then `net stop was /y && net start w3svc`.
2. Create an IIS site pointing at `C:\inetpub\agentstudio`, port 80/443, bound to the internal
   interface/hostname. The generated `web.config` uses:

   ```xml
   <aspNetCore processPath="dotnet" arguments=".\AgentStudio.Web.dll"
               hostingModel="inprocess" />
   ```

   (`AspNetCoreModuleV2` — this is the default from `dotnet publish`; no changes needed.)
3. Set the Application Pool to **No Managed Code**.
4. Set `ASPNETCORE_ENVIRONMENT=Production` (web.config `<environmentVariables>` or app-pool env).
5. **WebSockets are not required** — Blazor interactive-server normally uses them, but all
   streaming chat paths (widget + public chat) use SSE over plain HTTP POST, so you may leave
   the WebSocket protocol disabled/uninstalled. If you do enable the Blazor studio UI through
   a proxy, enable WebSockets for SignalR; SSE endpoints only need response buffering disabled
   (see below).

### Reverse proxy notes (ARR / nginx in front)

- Disable response buffering for `/api/agents/*/versions/*/stream` (SSE). For nginx:
  `proxy_buffering off;` and `proxy_read_timeout 300s;` on that location.
- Restrict the studio UI (`/`, `/agents`, …) to internal IPs; expose only
  `/api/agents/*/versions/*`, `/widget/*`, `/chat/*` and `/run/*` publicly if embedding the widget.

## 4. HTTP tool / SSRF

Outbound calls from workflow HTTP nodes block loopback/private IPs by default. To allow
internal services, add to `appsettings.Production.json`:

```json
{ "HttpTool": { "AllowedHosts": [ "internal-api.corp.local" ] } }
```

## 5. Backups (file copy cron)

The whole app storage is one SQLite file — back it up with a plain file copy, not a database
tool. Use SQLite's own `.backup` (via the `sqlite3` CLI, or `VACUUM INTO` through any SQLite
client) rather than `cp` on a live file, so a backup never runs mid-write:

```cron
# /etc/cron.d/agentstudio-backup — nightly at 02:30, 14 days retention
30 2 * * * appuser sqlite3 /path/to/agentstudio.db ".backup /var/backups/agentstudio/agentstudio-$(date +\%F).db" && find /var/backups/agentstudio -name '*.db' -mtime +14 -delete
```

Restore: stop the app, replace `agentstudio.db` with the backup file, start the app again.

Also back up `appsettings.Production.json` (contains provider API keys) — accounts (`Users`)
are config-only, not in the database, so that file is the only backup they need.

Since phase 2, `Conversations` (chat history) live in this same SQLite file — the backup above
already covers them, no separate step needed. Conversations have no auto-expiry by default, so
this table grows without bound unless you opt into the purge job below.

### Conversation retention (opt-in)

`ConversationRetentionService` (`AgentStudio.Infrastructure`) periodically deletes conversations
whose last activity is older than a configured age. **Disabled by default** — the no-TTL,
survives-a-restart behavior documented above is the default; this only bounds growth for
operators who choose to turn it on:

```json
{
  "ConversationRetention": {
    "Enabled": true,
    "RetentionDays": 90
  }
}
```

Runs once at startup and then every 24h (`CheckInterval`, also configurable, not usually needed).
Deleted conversations are gone — there's no separate archive.

### Scheduled agent triggers (on by default)

`ScheduledRunner` (`AgentStudio.Infrastructure`) polls every 30s for agents with a schedule
turned on (per-agent, from the agent's own page) and fires a fresh run of the latest published
version. Unlike conversation retention, the poll loop itself is **enabled by default** — the
per-agent `ScheduleEnabled` flag is the real switch operators use; there'd be no point leaving
the loop off by default. To disable the whole mechanism (e.g. a read-replica instance that
shouldn't fire triggers):

```json
{
  "ScheduledRunner": { "Enabled": false }
}
```

Single-process, no distributed lock: running more than one `AgentStudio.Web` instance against
the same database means every instance's poll loop fires the same due schedules independently
— each publishes its own duplicate run. Fine for one instance; don't turn schedules on if you're
already running more than one instance against a shared database without addressing this.

Uploaded RAG documents are **not** in the database — only their chunk text/embeddings are (in
`DocumentChunks`); the raw files live on the local filesystem at `Documents:StoragePath`
(default `App_Data/documents` under the app's content root). Back that directory up separately,
or set `Documents:StoragePath` to a path that's already covered by your filesystem backup.

## 6. Smoke test after deploy

1. Confirm `appsettings.Production.json` has at least one `Users` entry (an Admin), then sign in
   at `/login`.
2. Confirm `ModelProviders` has the provider(s) agents will use, create an agent, publish a
   version.
3. `curl -N -X POST https://host/api/agents/{id}/versions/1/stream -H "X-Agent-Api-Key: ask_..." -H "Content-Type: application/json" -d '{"message":"hi"}'` — expect SSE `data:` events (no login needed for this one — API-key protected only).
4. Check logs: IIS stdout log (`stdoutLogEnabled="true"` in web.config) or Windows Event Log.
