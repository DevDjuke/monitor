# Monitor

A self-hostable operations and observability control plane for autonomous software: AI agents, MCP servers, bots, workflows, scheduled jobs, scrapers, and background services.

Monitor starts with one deliberately small operational model:

```text
MonitoredComponent
  └─ AgentRun
       └─ TraceSpan
       └─ FailureGroup
            ├─ FailureAlertRule
            └─ FailureAlertEvent
                 └─ AlertDelivery
                      └─ AlertDeliveryDestination

RunAggregate
```

The goal is to make every autonomous component answer the same questions: Is it alive? What is it doing? What did it do? How long did it take? What tools/models did it call? How many tokens did it use? What did it cost? What failed, is that failure recurring, has it crossed an operational threshold, and was that alert actually delivered?

## Current vertical slice

- Component registry with environment, version, type, enabled state, and heartbeat.
- Run ingestion with input/output, model, token usage, cost, failure state, and timing.
- Nested trace spans for agent/model/tool/http/internal work.
- Standard OTLP/HTTP protobuf trace ingestion at `POST /v1/traces`.
- OpenTelemetry resource/trace/span mapping into the same Component -> Run -> Span model.
- GenAI semantic attributes mapped into model and token usage fields.
- Deterministic failure categories/fingerprints with occurrence and first/last-seen tracking.
- Failure-group drill-down with raw occurrence history, rolling rates, and a 24-hour hourly recurrence trend.
- Persistent recurrence alert rules with threshold/window/cooldown semantics.
- Durable alert events with acknowledgement audit state and duplicate-evidence suppression.
- Transactional alert-delivery outbox rows created together with each alert event.
- HMAC-SHA256 signed webhook delivery with encrypted signing secrets, retries, permanent-failure handling, dead letters, and manual requeue.
- `/alerts` operational queue for alert events, rule state, webhook destinations, delivery health, and delivery history.
- Runs history with server-side search/filtering and stable keyset pagination.
- SignalR-backed live run updates: the latest page refreshes automatically while older history remains stable.
- Hourly durable run aggregates by component and model for long-range usage metrics.
- Automated retention that purges only old, already-aggregated successful runs while preserving failed/cancelled forensic detail.
- `/usage` retention, aggregate, and recurring-failure dashboard without double-counting retained raw runs.
- Private Razor control plane protected by ASP.NET Core Identity/cookie authentication.
- Separate API-key authentication for autonomous components and OTLP exporters.
- One-time local owner setup and production bootstrap administrator support.
- `Monitor.Client` .NET SDK for registration, heartbeats, runs, spans, completion, cancellation, and API errors.
- `Monitor.SampleWorker` dogfoods the Monitor-native SDK.
- `Monitor.OtlpSampleWorker` dogfoods the standard OpenTelemetry .NET OTLP exporter without referencing `Monitor.Client`.
- Versioned EF Core migrations.
- SQL Server persistence with LocalDB as the default development instance.
- GitHub Actions SQL Server-backed integration tests for telemetry, OTLP, migration upgrades, keyset pagination, failure grouping, alert evaluation, duplicate-trigger protection, signed webhook delivery, retry/dead-letter behavior, and retention safety.

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

The old SQLite `monitor.db` file from the prototype is no longer used and can be deleted once you are sure it contains nothing you want to keep.

## OTLP trace ingestion

Monitor accepts OpenTelemetry trace exports at:

```text
POST /v1/traces
Content-Type: application/x-protobuf
X-Monitor-Key: <Monitor__IngestionApiKey>
```

The current OTLP surface supports **OTLP/HTTP + Protocol Buffers traces**. Optional gzip request compression is accepted. OTLP/JSON, OTLP/gRPC, metrics, and logs are not implemented yet.

A standard .NET OpenTelemetry exporter can point directly at Monitor:

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
        options.Headers = "X-Monitor-Key=replace-with-a-long-random-secret";
    })
    .Build();
```

The mapping is intentionally straightforward:

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

OTLP trace/span identifiers are retained alongside Monitor's internal GUIDs. Separately exported child and root spans for the same trace are merged into the same run. OTLP ingestion is serialized with a SQL Server application lock so retries/concurrent exporters cannot create competing identities inside a Monitor database.

To dogfood the standard exporter locally, run:

```powershell
$env:Monitor__IngestionApiKey = "replace-with-a-long-random-secret"
$env:Monitor__BaseUrl = "http://localhost:5000"
dotnet run --project samples/Monitor.OtlpSampleWorker
```

That sample emits one successful trace and two rate-limit failures through OpenTelemetry itself. The two failures contain different request ids but collapse into the same failure fingerprint while their raw error messages remain distinct.

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

The original `Run.Error`, payloads, span errors, attributes, and exception data are not rewritten. **A fingerprint is an index into failure evidence, not a replacement for it.**

Both Monitor-native failed completions and OTLP failures are grouped immediately. A background worker also backfills any failed/cancelled run that somehow remains ungrouped.

The recurring-failure table on `/usage` links to `/failures/{id}`. A failure drill-down shows:

- the stable SHA-256 fingerprint and normalized message template;
- category, failure/error type, dependency/provider and HTTP status;
- total occurrence count and first/last seen timestamps;
- rolling 15-minute, 1-hour and 24-hour occurrence counts;
- a fixed 24-hour hourly recurrence trend;
- up to 50 recent raw failed/cancelled runs with their original error messages;
- alert rules and alert-event history for that exact fingerprint.

## Failure alerting

Alert rules are persistent and scoped to one failure fingerprint. Their condition is:

```text
N matching failed/cancelled runs within the last M minutes
```

Each rule has:

- `Threshold`
- `WindowMinutes`
- `CooldownMinutes`
- enabled/disabled state
- last evaluation and trigger timestamps
- `LastTriggeredRunSequence`

The last triggering sequence is important: if a condition remains true, repeatedly evaluating it—or restarting the web process—does not fire the same evidence again. A newer matching run must exist. The cooldown separately controls how quickly genuinely new failures may produce another alert while the threshold condition remains true.

When a rule fires, Monitor persists a `FailureAlertEvent` containing the evaluated window, observed occurrence count, threshold, latest run sequence and trigger time. Operators can acknowledge the event; acknowledgement records the user and timestamp but does not mutate or remove the underlying failure evidence.

The `/alerts` page is the central queue for open/recent alert events and configured rule state. Rules can also be created, enabled or disabled from the associated failure-group page.

Configure evaluation through `appsettings.json` or environment variables:

```json
{
  "FailureAlerting": {
    "Enabled": true,
    "InitialDelaySeconds": 10,
    "SweepIntervalSeconds": 30
  }
}
```

Equivalent environment variables:

```text
FailureAlerting__Enabled
FailureAlerting__InitialDelaySeconds
FailureAlerting__SweepIntervalSeconds
```

The evaluator uses the SQL Server application lock `Monitor.FailureAlerting`, so only one Monitor web node evaluates rules at a time.

## Alert delivery

Webhook destinations are configured from `/alerts`. A destination has a name, HTTP/HTTPS endpoint, signing secret, enabled state, delivery health and historical delivery rows. The signing secret is stored through ASP.NET Core Data Protection rather than as plaintext in the Monitor database.

When an alert rule fires, its `FailureAlertEvent` and one `AlertDelivery` row for every currently enabled destination are saved together. This is the outbox boundary: a crash cannot commit an alert event while silently losing the fact that it still needs notification delivery.

Webhook requests contain a JSON payload with the alert event, rule, recurrence window and failure-group signature. Monitor also sends:

```text
X-Monitor-Event: failure.alert.triggered
X-Monitor-Delivery-Id: <stable delivery GUID>
X-Monitor-Timestamp: <unix timestamp seconds>
X-Monitor-Signature: sha256=<hex HMAC>
```

The signature is HMAC-SHA256 over the exact UTF-8 bytes of:

```text
<timestamp>.<request body>
```

A receiver should verify the signature, reject timestamps outside an acceptable replay window, and treat `X-Monitor-Delivery-Id` as an idempotency key. Delivery is **at least once**: if a receiver accepts a request but the response is lost before Monitor records success, the same delivery id can be sent again.

Retry behavior is deliberately operational rather than transport-specific:

- `2xx` marks the delivery delivered;
- timeout/network errors, `408`, `429`, and `5xx` are retried with exponential backoff;
- other `4xx` responses are treated as permanent and move directly to dead letter;
- retryable failures move to dead letter once `MaxAttempts` is reached;
- operators can manually requeue non-delivered rows from `/alerts`;
- disabling a destination pauses its queued deliveries without deleting them.

The delivery worker uses the SQL Server application lock `Monitor.AlertDelivery`, so only one Monitor web node dispatches the outbox at a time.

Configure delivery through `appsettings.json` or environment variables:

```json
{
  "AlertDelivery": {
    "Enabled": true,
    "SweepIntervalSeconds": 5,
    "BatchSize": 50,
    "MaxAttempts": 6,
    "BaseRetrySeconds": 10,
    "MaxRetryMinutes": 30,
    "RequestTimeoutSeconds": 10
  }
}
```

Equivalent environment variables:

```text
AlertDelivery__Enabled
AlertDelivery__SweepIntervalSeconds
AlertDelivery__BatchSize
AlertDelivery__MaxAttempts
AlertDelivery__BaseRetrySeconds
AlertDelivery__MaxRetryMinutes
AlertDelivery__RequestTimeoutSeconds
```

For multi-node Monitor deployments, ASP.NET Core Data Protection keys must be shared/persisted across nodes so every dispatcher node can decrypt the stored webhook signing secrets. The SQL application lock prevents concurrent dispatch, but it does not replace a shared Data Protection key ring.

Webhook is the first delivery adapter. Email, Slack, Teams, Discord, PagerDuty-style integrations and similar channels can be added on top of the same durable `AlertDelivery` contract without coupling them to failure detection.

## Retention and aggregation

Terminal runs are aggregated into durable hourly buckets keyed by UTC hour, component, and model. Buckets preserve run counts by terminal status, token totals, reported cost, and duration statistics.

Aggregation is idempotent at the run level: a terminal run receives `AggregatedAt` only in the same transaction that commits its contribution to an aggregate. A successful run is therefore never purge-eligible before its metrics have been durably counted.

The default policy is:

- aggregate terminal runs after 5 minutes;
- retain successful raw run/span detail for 30 days;
- run the retention sweep every 15 minutes;
- retain failed and cancelled run/span/error detail indefinitely.

Only successful runs are deleted. Failed and cancelled runs still contribute to aggregates but remain available in `/runs` for forensic inspection, including their payloads, error reason, spans, failure-group relationship, and alert evidence.

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

The authenticated `/usage` page reports durable aggregate totals plus only terminal raw runs that have not yet been aggregated. This means the overlap between aggregate data and retained successful raw detail does not double-count usage. The same page surfaces the highest-occurrence failure fingerprints.

## Run the Monitor.Client sample worker

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

SignalR notifies the browser when a run starts or completes, including OTLP-created and OTLP-updated runs. On the latest page, the matching filtered slice refreshes automatically without a page navigation. When browsing older pages, Monitor leaves the current rows in place and surfaces a new-activity banner instead of shifting history underneath the operator.

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

Every Monitor-native monitoring endpoint except `GET /api/health` requires either an authenticated Monitor browser session or the ingestion API key. OTLP `/v1/traces` requires the ingestion API key.

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
  Monitor.Domain/          protocol-independent monitoring + failure/alert/delivery model
  Monitor.Client/          .NET Monitor-native ingestion client SDK
  Monitor.Infrastructure/  EF Core persistence, retention, failure grouping/alerting + Identity store
  Monitor.Web/             HTTP/OTLP ingestion + Razor control plane + webhook delivery worker
samples/
  Monitor.SampleWorker/        synthetic Monitor.Client BackgroundService
  Monitor.OtlpSampleWorker/    standard OpenTelemetry OTLP exporter sample
docs/
  architecture.md
```

## Next

1. Additional alert delivery adapters: email, Slack, Teams, Discord/PagerDuty-style integrations as useful.
2. OTLP metrics and logs, followed by OTLP/JSON and gRPC transports where useful.
3. Per-component credentials and key rotation.
4. Cost/model dashboards and longer-range aggregate rollups.
5. Control-plane commands (pause, disable, kill run, configuration).