# Monitor

A self-hostable operations and observability control plane for autonomous software: AI agents, MCP servers, bots, workflows, scheduled jobs, scrapers, and background services.

Monitor starts with one deliberately small model:

```text
MonitoredComponent
  └─ AgentRun
       └─ TraceSpan
```

The goal is to make every autonomous component answer the same questions: Is it alive? What is it doing? What did it do? How long did it take? What tools/models did it call? How many tokens did it use? What did it cost? What failed?

## Current vertical slice

- Component registry with environment, version, type, enabled state, and heartbeat.
- Run ingestion with input/output, model, token usage, cost, failure state, and timing.
- Nested trace spans for agent/model/tool/http/internal work.
- Private Razor control plane protected by ASP.NET Core Identity/cookie authentication.
- Separate API-key authentication for autonomous components.
- One-time local owner setup and production bootstrap administrator support.
- Versioned EF Core migrations.
- SQL Server persistence with LocalDB as the default development instance.
- GitHub Actions build and SQL Server-backed startup/authentication smoke test.

This first API is intentionally simple HTTP. Native OpenTelemetry/OTLP ingestion is the next transport; the domain model is kept independent from the ingestion protocol.

## Run locally

Requirements:

- .NET 10 SDK
- SQL Server LocalDB available as `(localdb)\MSSQLLocalDB`, or another SQL Server instance supplied through `ConnectionStrings__Monitor`

The default development connection string is:

```text
Server=(localdb)\MSSQLLocalDB;Database=Monitor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True
```

Run Monitor:

```bash
dotnet restore Monitor.sln
dotnet run --project src/Monitor.Web
```

EF Core creates the `Monitor` database and applies pending migrations on startup. In Development, open `/account/setup` on the first run and create the owner account. The setup endpoint becomes unavailable as soon as a user exists.

The old SQLite `monitor.db` file from the prototype is no longer used and can be deleted once you are sure it contains nothing you want to keep.

Set an ingestion key before posting telemetry. Environment variables are preferred so secrets do not enter source control:

```bash
# Bash
export Monitor__IngestionApiKey="replace-with-a-long-random-secret"

# PowerShell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
```

For Production, public first-user setup is disabled. Bootstrap the first administrator through environment variables:

```bash
export Monitor__BootstrapAdmin__Email="owner@example.com"
export Monitor__BootstrapAdmin__Password="use-a-strong-password-here"
```

After the first account exists, those bootstrap values are ignored and can be removed from the environment.

## API authentication

Every monitoring endpoint except `GET /api/health` requires either an authenticated Monitor browser session or the ingestion API key:

```text
X-Monitor-Key: <Monitor__IngestionApiKey>
```

## Register a component

```bash
curl -X POST http://localhost:5000/api/components/register \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "name": "Website Auditor",
    "slug": "website-auditor",
    "type": "Agent",
    "environment": "development",
    "version": "0.1.0"
  }'
```

Then send a heartbeat using the returned component id:

```bash
curl -X POST http://localhost:5000/api/components/{componentId}/heartbeat \
  -H "X-Monitor-Key: $MONITOR_KEY"
```

## Start a run

```bash
curl -X POST http://localhost:5000/api/runs \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "componentId": "{componentId}",
    "name": "Audit website",
    "trigger": "Manual",
    "model": "gpt-5.6"
  }'
```

Add spans while the run is executing:

```bash
curl -X POST http://localhost:5000/api/runs/{runId}/spans \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "name": "Fetch homepage",
    "kind": "Http",
    "status": "Success",
    "startedAt": "2026-08-08T00:00:00Z",
    "completedAt": "2026-08-08T00:00:00.400Z"
  }'
```

Complete it:

```bash
curl -X POST http://localhost:5000/api/runs/{runId}/complete \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "status": "Success",
    "inputTokens": 8421,
    "outputTokens": 2119,
    "costUsd": 0.041,
    "outputJson": "{\"result\":\"ok\"}"
  }'
```

## Structure

```text
src/
  Monitor.Domain/          protocol-agnostic monitoring model
  Monitor.Infrastructure/  EF Core persistence + Identity store
  Monitor.Web/             secured HTTP ingestion API + Razor control plane

docs/
  architecture.md
```

## Next

1. OTLP receiver / OpenTelemetry semantic-convention mapping.
2. Live activity stream using SignalR or SSE.
3. Per-component credentials and key rotation.
4. Retention, aggregation, and cost/model dashboards.
5. Control-plane commands (pause, disable, kill run, configuration).
6. A tiny .NET client package and sample worker to dogfood the ingestion API.
