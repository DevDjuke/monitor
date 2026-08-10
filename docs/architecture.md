# Architecture

## Principle

Monitor is an observability system first and a control plane second. Components should not need to run inside Monitor and should not need to use the same language or agent framework.

The domain therefore does not depend on OpenAI, Anthropic, MCP, LangChain, Semantic Kernel, OpenTelemetry, or any specific runtime.

## Core model

### MonitoredComponent

A deployable or independently observable unit. Examples: agent, MCP server, Discord bot, workflow, scheduled job, scraper, background service.

Identity is `(Slug, Environment)`. A deployment can register repeatedly; registration is idempotent and updates mutable metadata.

For OTLP resources, Monitor derives the component from OpenTelemetry resource metadata. `service.name` is the display identity, `service.namespace` participates in the generated slug, `deployment.environment.name` selects the Monitor environment, and `service.version` updates the reported version.

### AgentRun

One execution or unit of work. This is the main operational object, rather than a log line. It owns timing, terminal status, model information, token usage, cost, input/output payloads, and error information.

Runs also have a database-generated monotonic `Sequence`. It is an operational ordering key used for stable keyset pagination and alert evidence ordering. Unlike timestamps or row versions, it does not change when a run completes and does not depend on two concurrent runs having distinct start times.

OTLP-backed runs additionally retain the 32-character OpenTelemetry trace id. OTLP retries and separately exported child/root spans are merged into the existing run by component + trace id while ingestion is serialized with a SQL Server application lock.

Terminal runs receive `AggregatedAt` only after their contribution to a durable aggregate has been committed in the same database transaction. Retention never removes a successful run whose `AggregatedAt` is null.

### TraceSpan

One nested operation inside a run. Spans can represent agent reasoning steps, model calls, tool calls, HTTP calls, or ordinary internal work. `ParentSpanId` permits a trace tree without coupling the domain to a telemetry vendor.

OTLP spans retain their external span id and parent span id in addition to the internal Monitor GUID relationship. Structured diagnostic fields include error type, HTTP status, model, token counts, and reported cost. The full OTLP attribute map is also retained as JSON for inspection. Exception events are promoted into the same structured error fields without discarding their attributes.

### FailureGroup

A durable grouping identity for recurring failed or cancelled runs. A group stores a deterministic SHA-256 fingerprint plus stable diagnostic dimensions: category, failure type, operation, dependency, HTTP status, a normalized message template, occurrence count, and first/last seen timestamps.

The classifier prefers structured signals such as `exception.type`, `error.type`, HTTP response status, dependency/resource attributes, and span kind. Raw messages are normalized only for fingerprinting by replacing unstable values such as URLs, GUIDs, long hexadecimal identifiers, quoted values, and numbers. The original run error and trace spans are never rewritten by classification.

Current categories are: Unknown, Timeout, RateLimit, Authentication, Authorization, Network, Http, Database, Validation, Serialization, ModelProvider, Tool, Dependency, Cancellation, and Internal.

Failure grouping is idempotent at the run level through `AgentRun.FailureGroupId`. Both OTLP ingestion and the custom HTTP completion path group failures immediately; a background worker periodically backfills any ungrouped failed/cancelled runs. A SQL Server application lock serializes grouping across Monitor nodes.

The authenticated `/failures/{id}` view is an operational projection over a group and its raw runs. It exposes the stable grouping signature, rolling 15-minute/1-hour/24-hour recurrence counts, a 24-hour hourly trend, alert rules/events, and direct links back to the latest raw forensic runs.

### FailureAlertRule

A persistent detection policy scoped to exactly one `FailureGroup`. A rule contains a threshold, rolling window, cooldown, enabled state, evaluation timestamps, and the last run sequence whose evidence caused a trigger.

The condition is deliberately explicit: **N occurrences of this fingerprint inside M minutes**. There is no message reclassification at alert time; the evaluator operates only on already-classified failed/cancelled runs linked to the group.

`LastTriggeredRunSequence` makes a trigger evidence-aware. Re-evaluating a still-true condition cannot produce another event unless at least one newer matching run exists. `CooldownMinutes` separately limits how quickly genuinely new evidence may result in another event.

### FailureAlertEvent

An immutable trigger record for a rule, except for acknowledgement metadata. It snapshots the trigger time, evaluated window, occurrence count, threshold, and latest run sequence that contributed to the trigger. `AcknowledgedAt` and `AcknowledgedBy` provide a minimal operator audit trail without deleting or resolving the underlying failure evidence.

Alert events are operational state, not notification-delivery receipts. Email, Slack, webhook, or other delivery adapters can consume these events later without changing the detection model.

### RunAggregate

A durable hourly metric bucket keyed by hour, component, and model. It stores terminal-run counts by status, input/output tokens, cost, total/min/max duration, and the first/last run timestamps represented by that bucket.

Component name and environment are snapshotted into the aggregate so historical reporting does not depend on later registration metadata changes. `ComponentId` remains the durable grouping identity.

## Transport

Monitor supports two complementary ingestion paths.

### Monitor HTTP API / `Monitor.Client`

The custom HTTP API remains the richer Monitor-native path for explicit component registration, heartbeats, run lifecycle, spans, and future control-plane-specific behavior. The .NET `Monitor.Client` wraps this API.

### OpenTelemetry / OTLP

Monitor accepts standard OTLP trace export requests at `POST /v1/traces` using OTLP/HTTP with Protocol Buffers (`application/x-protobuf` or `application/protobuf`). The endpoint uses the same `X-Monitor-Key` ingestion credential as the custom HTTP API and accepts optional gzip request compression.

The current OTLP mapping is:

- OpenTelemetry Resource -> `MonitoredComponent`;
- trace id -> `AgentRun`;
- root span -> run name/timing/terminal state;
- child spans -> `TraceSpan`;
- `gen_ai.request.model` / `gen_ai.response.model` -> model;
- `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` -> token totals;
- exception/error attributes and exception events -> structured diagnostic data;
- `http.response.status_code` -> HTTP failure signal;
- `monitor.cost_usd` -> optional reported cost extension.

A failed child span makes the enclosing Monitor run failed even when the root span itself was exported with an OK status. Late-arriving spans cause the run to be recomputed from the complete stored trace.

OTLP/JSON, OTLP/gRPC, metrics, and logs are deliberately not claimed yet. They can be added as additional transports/signals over the same protocol-independent domain.

## Failure alert evaluation

`FailureAlertingWorker` periodically invokes the scoped evaluator. The evaluator takes the SQL Server application lock `Monitor.FailureAlerting`, so only one Monitor web node evaluates rules at a time.

For every enabled rule it queries failed/cancelled raw runs for the linked fingerprint whose `CompletedAt` lies inside the rolling window. If the count reaches the threshold, cooldown has elapsed, and the newest matching run sequence is newer than `LastTriggeredRunSequence`, the evaluator creates one `FailureAlertEvent` and advances the rule's trigger markers in the same EF Core save boundary.

This gives three separate protections against alert storms:

1. fingerprinting collapses equivalent raw failures before alert evaluation;
2. run-sequence evidence tracking prevents repeated triggers from identical evidence, including after worker restarts;
3. cooldown limits how often genuinely new matching failures can generate alert events while a condition remains above threshold.

The evaluator intentionally reads retained raw failed/cancelled runs rather than aggregate buckets. Failure retention is therefore part of the alerting correctness contract. The `(FailureGroupId, CompletedAt, Sequence)` run index supports rolling-window evaluation and latest-evidence lookup.

The default worker policy is:

- enabled: true;
- startup delay: 10 seconds;
- evaluation interval: 30 seconds.

The `/alerts` page is the central operational queue for open/recent alert events and configured rule state. Acknowledgement is explicit operator state; it does not suppress future qualifying events and does not mutate the underlying failure group or runs.

## Realtime UI

The control plane uses SignalR as an in-process realtime notification channel. Persistence remains authoritative: a realtime event tells a browser that a run started or changed, then the browser re-queries the filtered run slice instead of reconstructing server query semantics client-side.

OTLP-created and OTLP-updated runs publish the same `RunChanged` notification used by Monitor-native ingestion.

The Runs history uses keyset pagination. The latest page can refresh automatically when matching runs change. Older pages remain stable; matching activity is surfaced as an update notification that can return the operator to the latest slice.

Alert events currently become visible on the next page request/refresh. A dedicated SignalR alert event can be added when the UI needs push notifications; persistence remains authoritative either way.

A distributed message bus or SignalR backplane is intentionally deferred until Monitor itself needs multiple web nodes. The domain should not depend on the chosen realtime transport.

## Persistence

SQL Server is the current persistence provider, with `(localdb)\MSSQLLocalDB` as the default development instance. `MonitorDbContext` remains isolated in Infrastructure so persistence concerns do not leak into the monitoring domain or UI contract.

OTLP trace/span identity columns use ordinary lookup indexes rather than filtered unique indexes. Idempotent OTLP upserts are enforced by the ingestion application lock and explicit identity lookup. This keeps ordinary SQL writers compatible without requiring filtered-index-specific session `SET` options.

Failure alert rules and events are durable database entities. Foreign keys use restrictive deletion so an alert audit trail cannot disappear as an accidental cascade from a rule or failure group.

## Retention and aggregation

Retention distinguishes normal successful telemetry from operational evidence.

The background retention worker first aggregates terminal runs into hourly `RunAggregate` buckets. Aggregation and setting `AgentRun.AggregatedAt` happen atomically. Re-running a sweep therefore does not double-count an already aggregated run.

Only `Success` is purge-eligible. A successful run can be deleted only when both conditions are true:

1. it has already been aggregated (`AggregatedAt != null`), and
2. its completion timestamp is older than the configured successful-run detail window.

Deleting a successful run cascades to its trace spans, which is where much of the raw telemetry volume is expected to live.

`Failed` and `Cancelled` runs also contribute to aggregate metrics, but their raw records are forensic evidence and are not purged by the retention worker. Their error/failure reason, input/output payloads, trace spans, and failure-group link remain available for later inspection. A failure fingerprint is an index into that evidence, never a replacement for it.

The worker uses a SQL Server application lock (`Monitor.RetentionAggregation`) so only one Monitor node can execute a retention sweep at a time. This prevents concurrent nodes from selecting and aggregating the same unmarked runs.

The default policy is intentionally conservative:

- aggregation delay: 5 minutes;
- successful raw-detail retention: 30 days;
- sweep interval: 15 minutes;
- failed/cancelled raw-detail retention: indefinite.

Changing the successful retention window affects future purge eligibility only. Aggregate buckets are not rewritten. Disabling the worker leaves raw data untouched.

The `/usage` page combines durable aggregate totals with only terminal runs whose `AggregatedAt` is still null. This prevents double-counting during the period where aggregated successful runs are intentionally retained in raw form. It also surfaces recurring failure groups by occurrence count and links directly into their drill-down pages while preserving links back to raw failed/cancelled runs.

Longer-term archival tiers or aggregate rollups (hourly -> daily/monthly) can be added when actual volume makes them useful; they must preserve the same forensic invariant for failed and cancelled runs.

## Control plane

Commands are intentionally absent from the first slice. Observability and detection must be trustworthy before Monitor is allowed to alter remote workloads. Future commands should be auditable entities with requested/accepted/completed states rather than fire-and-forget HTTP actions.
