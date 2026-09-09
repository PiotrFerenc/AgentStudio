# AgentStudio — Deployment (IIS + PostgreSQL)

Production topology: a single ASP.NET Core app (`AgentStudio.Web`) hosted in-process by IIS,
backed by PostgreSQL. The studio UI, the `/api/...` REST endpoints, the public chat page
(`/chat/{agentId}/{version}`), the public form page (`/run/{agentId}/{version}`, phase 3 —
a one-shot alternative to chat) and the embeddable widget (`/widget/agentstudio.js`) are all
served by this one app — there is no separate API process.

> **Security note — studio auth (phase 2).** The studio UI and the management `/api/...`
> endpoints require a logged-in account (cookie auth, Admin/Editor roles). On first start, with
> no accounts yet, `/` redirects to `/login` which redirects to `/setup` — create the first
> Admin there. Manage further accounts at `/users` (Admin only). The runtime agent endpoints
> (`/api/agents/{id}/versions/{v}/conversations` and `/stream`) stay anonymous, protected only
> by the per-agent `X-Agent-Api-Key` header — those (plus `/chat/*`, `/run/*` and `/widget/*`)
> are the endpoints safe to expose publicly; still bind the rest to an internal interface or a
> reverse proxy with IP restrictions.
>
> `/auth/login` and `/auth/setup` are rate-limited to 5 requests/minute per client IP (returns
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

## 2. PostgreSQL

### Option A — docker-compose (repo includes `docker-compose.yml`)

```bash
docker compose up -d postgres
```

This starts `postgres:17`, host port **5433** → container 5432 (remapped from the default 5432
because that's commonly already taken by another local Postgres on a dev box; on a dedicated
production host, either keep 5433 and match it in the connection string below, or edit
`docker-compose.yml` back to `"5432:5432"` if nothing else needs that port). Credentials/database
are in `docker-compose.yml`. Requires a docker-compose that supports the `3.9` file format
(the ancient `docker-compose` v1 binary does not — use the `docker compose` v2 plugin, or
`docker run` directly with the same image/env/volume as a fallback).

### Option B — native install

```sql
CREATE USER agentstudio WITH PASSWORD 'change-me';
CREATE DATABASE agentstudio OWNER agentstudio;
```

### Connection string

Create `appsettings.Production.json` next to `AgentStudio.Web.dll` (port must match whatever
your PostgreSQL is actually listening on — 5433 for Option A as configured above, 5432 for a
typical Option B / standalone install):

```json
{
  "ConnectionStrings": {
    "AgentStudio": "Host=localhost;Port=5432;Database=agentstudio;Username=agentstudio;Password=change-me"
  }
}
```

If agents use the `databaseQuery` workflow node (phase 3), also add a `DatabaseConnections`
array to the same file — these are config, not admin-panel data, so they're set once per
deployment rather than through the UI:

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

### Encrypting provider API keys at rest

`ModelProviderConfig.ApiKey` (the LLM provider's key, entered on `/providers`) is stored
encrypted (AES-256-GCM) once a key is configured — `SecretProtector` in
`AgentStudio.Infrastructure`, wired as an EF `ValueConverter`, so nothing outside that one
converter ever sees ciphertext. Set a 256-bit key, base64-encoded:

```bash
openssl rand -base64 32
```

Then either in `appsettings.Production.json`:

```json
{ "Secrets": { "EncryptionKey": "<base64 key>" } }
```

or as an environment variable (`__` for the nested key, standard ASP.NET Core config binding):

```bash
Secrets__EncryptionKey=<base64 key>
```

**No key configured = no encryption** (dev/CI default, not a silent downgrade — this is the
same opt-in posture as `ConversationRetention`). Losing the key makes every stored ApiKey
unrecoverable — back it up like any other production secret, separately from the database dump
(a DB backup without the key is useless for this column, and vice versa).

Rows written before a key was configured (or before this feature existed) stay plaintext in the
column — enabling the key encrypts new writes only, it doesn't retroactively re-encrypt existing
rows. `/providers` has no edit/delete today (create-only), so there's no UI path to force a
rewrite; migrating an existing provider's key means updating its `ApiKey` column directly in
Postgres (with the key already configured, so the app-side encryption logic is available if
scripted through the app rather than raw SQL).

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

## 5. Backups (pg_dump cron)

```cron
# /etc/cron.d/agentstudio-backup — nightly at 02:30, 14 days retention
30 2 * * * postgres pg_dump -Fc agentstudio > /var/backups/agentstudio/agentstudio-$(date +\%F).dump && find /var/backups/agentstudio -name '*.dump' -mtime +14 -delete
```

Restore: `pg_restore -d agentstudio --clean file.dump`.

Also back up `appsettings.Production.json` (contains DB password and provider keys).

Since phase 2, `Conversations` (chat history) and `Users` (accounts) live in this same database
— the backup above already covers them, no separate step needed. Conversations have no
auto-expiry by default, so this table grows without bound unless you opt into the purge job
below.

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

1. Open the studio UI — first visit redirects to `/setup`; create the first Admin account there,
   then sign in at `/login`.
2. Create a provider + agent, publish a version.
3. `curl -N -X POST https://host/api/agents/{id}/versions/1/stream -H "X-Agent-Api-Key: ask_..." -H "Content-Type: application/json" -d '{"message":"hi"}'` — expect SSE `data:` events (no login needed for this one — API-key protected only).
4. Check logs: IIS stdout log (`stdoutLogEnabled="true"` in web.config) or Windows Event Log.
