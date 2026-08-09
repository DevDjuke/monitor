# Monitor

A self-hostable operations and observability control plane for autonomous software: AI agents, MCP servers, bots, workflows, scheduled jobs, scrapers, and background services.

Monitor starts with one deliberately small model:

```text
MonitoredComponent
  └─ AgentRun
       └─ TraceSpan

RunAggregate
```

The goal is to make every autonomous component answer the same questions: Is it alive? What is it doing? What did it do? How long did it take? What tools/models did it call? How many tokens did it use? What did it cost? What failed?

## Current vertical slice

- Component registry with environment, version, type, enabled state, and heartbeat.
- Run ingestion with input/output, model, token usage, cost, failure state, and timing.
- Nested trace spans for agent/model/tool/http/internal work.
- Runs history with server-side search/filtering and stable keyset pagination.
- SignalR-backed live run updates: the latest page refreshes automatically while older history remains stable.
- Hourly durable run aggregates by component and model for long-range usage metrics.
- Automated retention that purges only old, already-aggregated successful runs while preserving failed/cancelled forensic detail.
- `/usage` retention and aggregate dashboard without double-counting retained raw runs.
- Private Razor control plane protected by ASP.NET Core Identity/cookie authentication.
- Separate API-key authentication for autonomous components.
- One-time local owner setup and production bootstrap administrator support.
- `Monitor.Client` .NET SDK for registration, heartbeats, runs, spans, completion, cancellation, and API errors.
- `Monitor.SampleWorker` dogfoods the SDK with recurring synthetic agent activity and intentional failures.
- Versioned EF Core migrations.
- SQL Server persistence with LocalDB as the default development instance.
- GitHub Actions build and SQL Server-backed end-to-end tests for telemetry, migration upgrades, keyset pagination, and retention safety.

This first API is intentionally simple HTTP. Native OpenTelemetry/OTLP ingestion is the next transport; the domain model is kept independent from the ingestion protocol.

## Run locally

Requirements:

- .NET 10 SDK
- SQL Server LocalDB available as `(localdb)\MSSQLLocalDB`, or another SQL Server instance supplied through `ConnectionStrings__Monitor`

The default development connection string is:

```text
Server=(localdb)\MSSQLLocalDB;Database=Monitor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True
```

Set an ingestion key and run Monitor:

```powershell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
dotnet restore Monitor.sln
dotnet run --project src/Monitor.Web
```

EF Core creates the `Monitor` database and applies pending migrations on startup. In Development, open `/account/setup` on the first run and create the owner account. The setup endpoint becomes unavailable as soon as a user exists.

The old SQLite `monitor.db` file from the prototype is no longer used and can be deleted once you are sure it contains nothing you want to keep.

## Retention and aggregation

Terminal runs are aggregated into durable hourly buckets keyed by UTC hour, component, and model. Buckets preserve run counts by terminal status, token totals, reported cost, and duration statistics.

Aggregation is idempotent at the run level: a terminal run receives `AggregatedAt` only in the same transaction that commits its contribution to an aggregate. A successful run is therefore never purge-eligible before its metrics have been durably counted.

The default policy is:

- aggregate terminal runs after 5 minutes;
- retain successful raw run/span detail for 30 days;
- run the retention sweep every 15 minutes;
- retain failed and cancelled run/span/error detail indefinitely.

Only successful runs are deleted. Failed and cancelled runs still contribute to aggregates but remain available in `/runs` for forensic inspection, including their payloads, error reason, and spans.

Configure the policy through `appsettings.json` or environment variables:

```json
{
  "Retention": {
    "Enabled": true,
    "AggregationDelayMinutes": 5,
    "SuccessfulRunDetailDays": 30,
    "SweepIntervalMinutes": 15,
    "BatchSize": 1000,
    "MaxBatchesPerSweep": 20
  }
}
```

Equivalent environment variables use the normal ASP.NET Core double-underscore syntax:

```text
Retention__Enabled
Retention__AggregationDelayMinutes
Retention__SuccessfulRunDetailDays
Retention__SweepIntervalMinutes
Retention__BatchSize
Retention__MaxBatchesPerSweep
```

Set `Retention__Enabled=false` to stop both aggregation and purging. Existing raw data and aggregate buckets are left untouched. Changing the successful-run retention window changes future purge eligibility; it does not rewrite historical aggregate buckets.

The retention worker uses a SQL Server application lock so only one Monitor web node performs a sweep at a time.

The authenticated `/usage` page reports durable aggregate totals plus only terminal raw runs that have not yet been aggregated. This means the 30-day overlap between aggregate data and retained successful raw detail does not double-count usage.

## Run the sample worker

In a second terminal, use the same ingestion key and point the worker at the Monitor URL:

```powershell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
$env:Monitor__BaseUrl = "http://localhost:5000"
dotnet run --project samples/Monitor.SampleWorker
```

If Monitor is listening on a different URL, set `Monitor__BaseUrl` to that address.

The worker registers as `sample-website-auditor`, sends a heartbeat every 15 seconds, and starts a synthetic website-audit run every 30 seconds. Each run emits HTTP, tool, model, and agent spans plus synthetic token/cost data. Every fifth run intentionally fails so the dashboard has both healthy and failed telemetry to display.

The sample does not call a real website or model provider; its delays and failures are synthetic so local development and CI remain deterministic and self-contained.

## Runs history

`/runs` loads data through the authenticated query API instead of rendering a fixed batch during the Razor page request. Available filters include component, status, environment, model, free-text search, date range, and page size.

Pagination is keyset-based. Each run receives a database-generated monotonic sequence and the next page asks for rows older than the last visible sequence. This keeps browsing stable while new telemetry is arriving and avoids increasingly expensive deep SQL `OFFSET` queries.

SignalR notifies the browser when a run starts or completes. On the latest page, the matching filtered slice refreshes automatically without a page navigation. When browsing older pages, Monitor leaves the current rows in place and surfaces a new-activity banner instead of shifting history underneath the operator.

## Client SDK

A component can use `Monitor.Client` directly:

```csharp
using Monitor.Client;
using Monitor.Domain;

var httpClient = new HttpClient
{
    BaseAddress = new Uri("http://localhost:5000")
};

var monitor = new MonitorClient(httpClient, ingestionApiKey);

var component = await monitor.RegisterComponentAsync(
    new ComponentRegistration(
        "Invoice Agent",
        "invoice-agent",
        ComponentType.Agent,
        "development",
        "0.1.0"));

await monitor.HeartbeatAsync(component.Id);

var run = await monitor.StartRunAsync(
    new StartRunOptions(
        component.Id,
        "Process invoices",
        Trigger: "Scheduled",
        Model: "example-model",
        Input: new { count = 4 }));

try
{
    await run.MeasureSpanAsync(
        "Load invoices",
        SpanKind.Tool,
        async cancellationToken =>
        {
            await Task.Delay(100, cancellationToken);
        });

    await run.CompleteAsync(
        new RunCompletion(
            InputTokens: 1200,
            OutputTokens: 340,
            CostUsd: 0.012,
            Output: new { processed = 4 }));
}
catch (Exception exception)
{
    await run.FailAsync(exception);
    throw;
}
```

`MonitorRun.MeasureSpanAsync` records the span duration and marks the span failed when the wrapped operation throws. If recording the failed span also fails, the SDK preserves the original application exception instead of replacing it with the telemetry error.

## Production bootstrap

Public first-user setup is disabled in Production. Bootstrap the first administrator through environment variables:

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

## Register a component over HTTP

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

## Start a run over HTTP

```bash
curl -X POST http://localhost:5000/api/runs \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "componentId": "{componentId}",
    "name": "Audit website",
    "trigger": "Manual",
    "model": "example-model"
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
  Monitor.Client/          .NET ingestion client SDK
  Monitor.Infrastructure/  EF Core persistence + Identity store
  Monitor.Web/             secured HTTP ingestion API + Razor control plane
samples/
  Monitor.SampleWorker/    synthetic monitored BackgroundService
docs/
  architecture.md
```

## Next

1. OTLP receiver / OpenTelemetry semantic-convention mapping.
2. Per-component credentials and key rotation.
3. Cost/model dashboards, alerting, and longer-range aggregate rollups.
4. Control-plane commands (pause, disable, kill run, configuration).
