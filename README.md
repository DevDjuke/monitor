# Monitor

A self-hostable operations and observability control plane for autonomous software: AI agents, MCP servers, bots, workflows, scheduled jobs, scrapers, and background services.

Monitor starts with one deliberately small operational model:

```text
MonitoredComponent
  ├─ LogEvent
  └─ AgentRun
       ├─ TraceSpan
       ├─ LogEvent
       └─ FailureGroup
            ├─ FailureAlertRule
            └─ FailureAlertEvent
                 └─ AlertDelivery
                      └─ AlertDeliveryDestination

RunAggregate
AuditEvent
```

The goal is to make every autonomous component answer the same questions: Is it alive? What is it doing? What did it do? How long did it take? What tools/models did it call? What did it log? How many tokens did it use? What did it cost? What failed, is that failure recurring, has it crossed an operational threshold, was that alert delivered, and who changed the control-plane state around it?

## Current vertical slice

- Component registry with environment, version, type, enabled state, and heartbeat.
- Per-component ingestion credentials with one-time plaintext issue/rotation, hash-only persistence, last-used tracking, revocation, and component-scope enforcement.
- Run ingestion with input/output, model, token usage, cost, failure state, and timing.
- Nested trace spans for agent/model/tool/http/internal work.
- Structured log/run events with level, message/template, properties, exception detail, source, and optional run/span correlation.
- Standard OTLP/HTTP protobuf trace ingestion at `POST /v1/traces`.
- Standard OTLP/HTTP protobuf log ingestion at `POST /v1/logs`.
- OpenTelemetry resource/trace/span/log mapping into the same component/run/span/event model.
- GenAI semantic attributes mapped into model and token usage fields.
- `/logs` server-side filtering by time, component, environment, severity, source, run, span, and text.
- Timestamp-ordered run timeline that merges spans and structured events while retaining the trace tree.
- Deterministic failure categories/fingerprints with occurrence and first/last-seen tracking.
- Failure-group drill-down with raw occurrence history, rolling rates, and a 24-hour hourly recurrence trend.
- Persistent recurrence alert rules with threshold/window/cooldown semantics and per-rule destination assignment.
- Durable alert events with acknowledgement state and duplicate-evidence suppression.
- Transactional alert-delivery outbox rows created together with each alert event.
- HMAC-SHA256 signed webhook delivery with encrypted signing secrets, retries, permanent-failure handling, dead letters, and manual requeue.
- `/alerts` operational queue for alert events, rule state, webhook destinations, delivery health, and delivery history.
- Append-only `AuditEvent` records for operator control-plane mutations with actor/action/target identity and safe before/after/metadata snapshots.
- Transactional audit writes: operational mutation and audit evidence share one EF Core/SQL Server save boundary.
- `/audit` server-side filtering by time, actor, action, target, target id, and text with expandable structured change snapshots.
- Secret-safe audit policy: credential plaintext/hash and webhook protected signing secrets are never copied into audit JSON.
- Audit evidence independent of mutable targets: no operational foreign keys or telemetry-retention cascades can erase an audit row.
- Runs history with server-side search/filtering and stable keyset pagination.
- SignalR-backed live run updates: the latest page refreshes automatically while older history remains stable.
- Hourly durable run aggregates by component and model for long-range usage metrics.
- Automated retention that purges only old, already-aggregated successful runs while preserving failed/cancelled forensic detail.
- Bounded retention for unlinked/component-only logs so ordinary logging cannot grow indefinitely.
- `/usage` retention, aggregate, and recurring-failure dashboard without double-counting retained raw runs.
- Private Razor control plane protected by ASP.NET Core Identity/cookie authentication.
- Bootstrap ingestion key plus scoped API-key authentication for autonomous components and OTLP exporters.
- One-time local owner setup and production bootstrap administrator support.
- `Monitor.Client` .NET SDK for registration, heartbeats, runs, spans, structured events, completion, cancellation, and API errors.
- `Monitor.SampleWorker` dogfoods the Monitor-native SDK.
- `Monitor.OtlpSampleWorker` dogfoods the standard OpenTelemetry .NET trace and logging exporters without referencing `Monitor.Client`.
- Versioned EF Core migrations.
- SQL Server persistence with LocalDB as the default development instance.
- GitHub Actions SQL Server-backed integration tests for telemetry, traces, logs, migration upgrades, keyset pagination, credentials, failure grouping, alert evaluation, signed webhook delivery, retry/dead-letter behavior, retention safety, and audit atomicity/secret exclusion.

The Monitor-native HTTP API and OTLP are complementary. The custom API carries Monitor-specific lifecycle semantics; OTLP provides vendor-neutral observability ingestion.

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

The shared `Monitor__IngestionApiKey` remains the privileged bootstrap/migration path. For normal component traffic, issue a scoped credential from `/components/{id}`. The current development secret setup is intentionally temporary; vault/secret-store migration remains explicit roadmap security debt.

## OTLP trace and log ingestion

Monitor accepts standard OpenTelemetry exports at:

```text
POST /v1/traces
POST /v1/logs
Content-Type: application/x-protobuf
X-Monitor-Key: <bootstrap or component ingestion key>
```

`application/protobuf` and optional gzip request compression are accepted. The current standard surface is **OTLP/HTTP + Protocol Buffers traces and logs**. OTLP/HTTP JSON, OTLP/gRPC, and metrics are not implemented yet.

A standard .NET OpenTelemetry trace exporter can point directly at Monitor:

```csharp
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using var provider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(
        ResourceBuilder.CreateDefault()
            .AddService("Invoice Agent", serviceVersion: "1.0.0")
            .AddAttributes([
                new KeyValuePair<string, object>("deployment.environment.name", "production")
            ]))
    .AddSource("Invoice.Agent")
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri("http://localhost:5000/v1/traces");
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Headers = "X-Monitor-Key=replace-with-an-ingestion-key";
    })
    .Build();
```

The trace mapping is intentionally straightforward:

```text
OpenTelemetry Resource       -> MonitoredComponent
trace id                     -> AgentRun
root span                    -> run name/timing/status
child span                   -> TraceSpan
service.name                 -> component name
deployment.environment.name  -> environment
service.version              -> component version
gen_ai.* model attributes    -> model
gen_ai.usage.*_tokens        -> token totals
exception.type / error.type  -> failure diagnostics
http.response.status_code    -> HTTP failure signal
```

OTLP trace/span identifiers are retained alongside Monitor's internal GUIDs. Separately exported child and root spans for the same trace are merged into the same run. OTLP trace ingestion is serialized with a SQL Server application lock so retries/concurrent exporters cannot create competing identities inside a Monitor database.

A standard .NET logging provider can export to Monitor independently of `Monitor.Client`:

```csharp
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService("Invoice Agent", serviceVersion: "1.0.0"));
        options.IncludeFormattedMessage = true;
        options.ParseStateValues = true;
        options.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri("http://localhost:5000/v1/logs");
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = "X-Monitor-Key=replace-with-an-ingestion-key";
        });
    });
});
```

OTLP logs map resource metadata to the component, instrumentation scope to `Source`, severity to `LogEventLevel`, body to the message, attributes to structured properties, `exception.*` to exception detail, and trace/span ids to run/span correlation. If a log arrives before its trace, Monitor retains the external ids and a background correlation worker attaches it after the trace appears.

OTLP log retries use a deterministic SHA-256 dedupe identity under a SQL application lock. This suppresses retransmission of the same record without collapsing independent real events that happen to share message text.

To dogfood both standard exporters locally, run:

```powershell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
$env:Monitor__BaseUrl = "http://localhost:5000"
dotnet run --project samples/Monitor.OtlpSampleWorker
```

That sample emits one successful trace, two rate-limit failures, and correlated structured logs through OpenTelemetry itself. The two failures contain different request ids but collapse into the same failure fingerprint while their raw run errors and log records remain distinct.

Detailed log/event behavior is documented in `docs/logs-and-run-events.md`.

## Failure fingerprints and drill-down

Failed and cancelled runs are classified into a durable `FailureGroup`. The classifier prefers structured telemetry over message text and currently recognizes:

- Timeout
- RateLimit
- Authentication
- Authorization
- Network
- Http
- Database
- Validation
- Serialization
- ModelProvider
- Tool
- Dependency
- Cancellation
- Internal
- Unknown

A fingerprint is a SHA-256 hash over stable dimensions such as category, exception/error type, operation, dependency, HTTP status, and a normalized message template. Volatile values such as URLs, GUIDs, long hexadecimal identifiers, quoted values, and numbers are replaced only in the template used for grouping.

The original `Run.Error`, payloads, span errors, attributes, exception data, and log evidence are not rewritten. **A fingerprint is an index into failure evidence, not a replacement for it.**

Both Monitor-native failed completions and OTLP failures are grouped immediately. A background worker also backfills any failed/cancelled run that somehow remains ungrouped.

The recurring-failure table on `/usage` links to `/failures/{id}`. A failure drill-down shows the stable fingerprint dimensions, rolling 15-minute/1-hour/24-hour occurrence counts, a 24-hour hourly trend, raw failed/cancelled runs, alert rules, and alert-event history.

## Failure alerting

Alert rules are persistent and scoped to one failure fingerprint. Their condition is:

```text
N matching failed/cancelled runs within the last M minutes
```

Each rule has threshold, rolling window, cooldown, enabled/deleted state, last evaluation/trigger metadata, `LastTriggeredRunSequence`, and delivery destination scope.

The last triggering sequence makes alerts evidence-aware: if a condition remains true, repeatedly evaluating it—or restarting the web process—does not fire the same evidence again. A newer matching run must exist. Cooldown separately controls how quickly genuinely new failures may produce another alert.

When a rule fires, Monitor persists a `FailureAlertEvent` containing the evaluated window, observed occurrence count, threshold, latest run sequence and trigger time. Operators can acknowledge the event; acknowledgement records the user and timestamp but does not mutate or remove the underlying failure evidence.

Rule CRUD lives at `/alerts/rules`; the `/alerts` page is the central operational queue for alert events and delivery state.

## Alert delivery

Webhook destinations are configured from `/alerts`. A destination has a name, HTTP/HTTPS endpoint, signing secret, enabled state, delivery health and historical delivery rows. The signing secret is stored through ASP.NET Core Data Protection rather than as plaintext in the Monitor database.

When an alert rule fires, its `FailureAlertEvent` and selected `AlertDelivery` outbox rows are saved together. A crash therefore cannot commit an alert while silently losing the fact that notification still needs delivery.

Webhook requests contain:

```text
X-Monitor-Event: failure.alert.triggered
X-Monitor-Delivery-Id: <stable delivery GUID>
X-Monitor-Timestamp: <unix timestamp seconds>
X-Monitor-Signature: sha256=<hex HMAC>
```

The HMAC-SHA256 signature covers `<timestamp>.<request body>`. Receivers should validate the timestamp/signature and use `X-Monitor-Delivery-Id` as an idempotency key because delivery is **at least once**.

Retry behavior:

- `2xx` -> delivered;
- timeout/network errors, `408`, `429`, and `5xx` -> exponential retry;
- other `4xx` -> permanent dead letter;
- retryable failures -> dead letter after `MaxAttempts`;
- operators can manually requeue non-delivered rows;
- disabling a destination pauses its queued deliveries without deleting them.

For multi-node Monitor deployments, ASP.NET Core Data Protection keys must be shared/persisted across nodes so every dispatcher can decrypt stored webhook signing secrets.

## Audit trail

`/audit` is the append-only control-plane change history. It is separate from telemetry and from alert/delivery state: a log describes workload behavior, while an audit row records who or what changed Monitor itself.

Each audit row stores the occurrence time, actor type/id/name, a stable action string, target type/id/name, and optional structured `BeforeJson`, `AfterJson`, and `MetadataJson` snapshots.

Current operator coverage includes:

- alert acknowledgement;
- alert-rule create/edit/enable/disable/delete;
- webhook destination create/enable/disable/test;
- alert-delivery requeue;
- component credential issue/rotate/revoke.

`AuditTrailWriter` stages the audit row in the same `MonitorDbContext` as the operational mutation. The handler then performs one `SaveChangesAsync`, so SQL Server commits or rolls back the change and its audit evidence together. The permanent audit CI gate proves this by deliberately rejecting an audit insert and verifying the corresponding operational mutation also rolls back.

Audit snapshots are allow-listed. Component credential plaintext/hashes and protected webhook signing secrets are never copied into audit JSON. `AuditEvents` has no foreign keys to operational targets, so target deletion or telemetry retention cannot cascade away history. The telemetry retention worker does not purge audit events.

Detailed invariants and extension guidance are in `docs/audit-trail.md`.

## Retention and aggregation

Terminal runs are aggregated into durable hourly buckets keyed by UTC hour, component, and model. Aggregation is idempotent at the run level: `AggregatedAt` is committed atomically with the run's contribution to the aggregate.

The default policy is:

- aggregate terminal runs after 5 minutes;
- retain successful raw run/span/log detail for 30 days;
- retain unlinked/component-only logs for 30 days;
- run the retention sweep every 15 minutes;
- retain failed and cancelled raw run/span/log/error detail indefinitely;
- keep audit records outside telemetry retention.

Only successful runs are deleted, and only after aggregation. Their run-linked spans and log events cascade with the raw run. Failed/cancelled runs and their linked telemetry remain forensic evidence. Unlinked logs have a separate bounded window because they do not have a run outcome.

Configure the telemetry policy through `appsettings.json` or environment variables:

```json
{
  "Retention": {
    "Enabled": true,
    "AggregationDelayMinutes": 5,
    "SuccessfulRunDetailDays": 30,
    "UnlinkedLogDetailDays": 30,
    "SweepIntervalMinutes": 15,
    "BatchSize": 1000,
    "MaxBatchesPerSweep": 20
  }
}
```

Equivalent environment variables use normal ASP.NET Core double-underscore syntax, including `Retention__SuccessfulRunDetailDays` and `Retention__UnlinkedLogDetailDays`.

Set `Retention__Enabled=false` to stop telemetry aggregation and purging. Existing raw data and aggregate buckets are left untouched.

## Run the Monitor.Client sample worker

In a second terminal, use the same ingestion key and point the worker at the Monitor URL:

```powershell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
$env:Monitor__BaseUrl = "http://localhost:5000"
dotnet run --project samples/Monitor.SampleWorker
```

The worker registers as `sample-website-auditor`, sends heartbeats, starts synthetic website-audit runs, emits HTTP/tool/model/agent spans plus structured run events and token/cost data, and intentionally fails every fifth run so the control plane has realistic success/failure evidence.

## Runs, logs, and audit history

`/runs` uses authenticated server-side filtering plus stable keyset pagination. Each run receives a database-generated monotonic sequence, so older history remains stable while new telemetry is arriving. SignalR refreshes the latest matching slice without shifting older pages underneath the operator.

`/logs` is also server-filtered and query-string driven. Filters include time window, component, environment, minimum severity, source, run, span, free-text search, and result size. Structured properties and exception detail remain expandable instead of being flattened into the display message.

Run drill-down merges spans and log events into one timestamp-ordered timeline while retaining the dedicated trace tree for parent/child execution structure.

`/audit` filters the durable control-plane record by time window, actor, action, target type/id, and free text. Before/after/metadata JSON remains expandable for forensic review.

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

await run.LogAsync(
    LogEventLevel.Information,
    "Invoice batch started.",
    new { count = 4 },
    source: "Invoice.Agent");

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
    await run.RecordEventAsync(new LogEventRecord(
        LogEventLevel.Error,
        exception.Message,
        ExceptionType: exception.GetType().FullName,
        ExceptionMessage: exception.Message,
        ExceptionStackTrace: exception.StackTrace,
        Source: "Invoice.Agent"));
    await run.FailAsync(exception);
    throw;
}
```

`MonitorRun.MeasureSpanAsync` records span duration and failure state. If recording failed telemetry itself fails, the SDK preserves the original application exception rather than replacing it with the telemetry error.

## Production bootstrap

Public first-user setup is disabled in Production. Bootstrap the first administrator through environment variables:

```bash
export Monitor__BootstrapAdmin__Email="owner@example.com"
export Monitor__BootstrapAdmin__Password="use-a-strong-password-here"
```

After the first account exists, those bootstrap values are ignored and can be removed from the environment.

## API authentication

Every Monitor-native monitoring endpoint except `GET /api/health` requires either an authenticated Monitor browser session or an ingestion credential. OTLP `/v1/traces` and `/v1/logs` require an ingestion credential.

```text
X-Monitor-Key: <bootstrap or component ingestion key>
```

A component-scoped key can only act on its owning component. The bootstrap key remains privileged during the migration period.

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

## Start a run and add detail over HTTP

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

Add a structured event:

```bash
curl -X POST http://localhost:5000/api/runs/{runId}/events \
  -H "Content-Type: application/json" \
  -H "X-Monitor-Key: $MONITOR_KEY" \
  -d '{
    "level": "Information",
    "message": "Homepage fetched",
    "messageTemplate": "Homepage fetched",
    "source": "Website.Auditor"
  }'
```

Add a span:

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
  Monitor.Domain/          protocol-independent monitoring + log/failure/alert/audit model
  Monitor.Client/          .NET Monitor-native ingestion client SDK
  Monitor.Infrastructure/  EF Core persistence, retention, auditing, grouping/alerting/correlation + Identity store
  Monitor.Web/             HTTP/OTLP ingestion + Razor control plane + background workers
samples/
  Monitor.SampleWorker/        synthetic Monitor.Client BackgroundService
  Monitor.OtlpSampleWorker/    standard OpenTelemetry trace + logging exporter sample
docs/
  architecture.md
  audit-trail.md
  component-ingestion-credentials.md
  logs-and-run-events.md
  roadmap.md
```

## Roadmap

The maintained implementation sequence is in `docs/roadmap.md`. Alert-rule management, component credentials, logs/run events, and the durable audit trail are complete. The next planned slice is **budgets and usage policy**, followed by component control commands.
