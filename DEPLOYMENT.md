# AgentStudio — Deployment (IIS + PostgreSQL)

Production topology: a single ASP.NET Core app (`AgentStudio.Web`) hosted in-process by IIS,
backed by PostgreSQL. The studio UI, the `/api/...` REST endpoints, the public chat page
(`/chat/{agentId}/{version}`) and the embeddable widget (`/widget/agentstudio.js`) are all
served by this one app — there is no separate API process.

> **Security note — studio auth (phase 2).** The studio UI and the management `/api/...`
> endpoints require a logged-in account (cookie auth, Admin/Editor roles). On first start, with
> no accounts yet, `/` redirects to `/login` which redirects to `/setup` — create the first
> Admin there. Manage further accounts at `/users` (Admin only). The runtime agent endpoints
> (`/api/agents/{id}/versions/{v}/conversations` and `/stream`) stay anonymous, protected only
> by the per-agent `X-Agent-Api-Key` header — those (plus `/chat/*` and `/widget/*`) are the
> endpoints safe to expose publicly; still bind the rest to an internal interface or a reverse
> proxy with IP restrictions, since there's no rate limiting or lockout on `/auth/login` yet.

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

EF Core migrations run automatically at startup (`MigrateAsync`) — no manual `dotnet ef database update`
is needed, but the DB user needs DDL rights on first start. The migration is
`AgentStudio.Infrastructure/Migrations/*_Initial.cs` (Npgsql provider, `jsonb` columns).

If `ConnectionStrings:AgentStudio` is empty or the literal `"InMemory"`, the app falls back to
EF InMemory (dev mode — data is lost on restart).

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
  `/api/agents/*/versions/*` and `/widget/*` and `/chat/*` publicly if embedding the widget.

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
auto-expiry, so this table grows without bound; there is no retention/purge job yet.

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
