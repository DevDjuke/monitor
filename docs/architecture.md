# Architecture

## Principle

Monitor is an observability system first and a control plane second. Components should not need to run inside Monitor and should not need to use the same language or agent framework.

The domain therefore does not depend on OpenAI, Anthropic, MCP, LangChain, Semantic Kernel, OpenTelemetry, or any specific runtime.

## Core model

### MonitoredComponent

A deployable or independently observable unit. Examples: agent, MCP server, Discord bot, workflow, scheduled job, scraper, background service.

Identity is `(Slug, Environment)`. A deployment can register repeatedly; registration is idempotent and updates mutable metadata.

### AgentRun

One execution or unit of work. This is the main operational object, rather than a log line. It owns timing, terminal status, model information, token usage, cost, input/output payloads, and error information.

Runs also have a database-generated monotonic `Sequence`. It is an operational ordering key used for stable keyset pagination. Unlike timestamps or row versions, it does not change when a run completes and does not depend on two concurrent runs having distinct start times.

Terminal runs receive `AggregatedAt` only after their contribution to a durable aggregate has been committed in the same database transaction. Retention never removes a successful run whose `AggregatedAt` is null.

### TraceSpan

One nested operation inside a run. Spans can represent agent reasoning steps, model calls, tool calls, HTTP calls, or ordinary internal work. `ParentSpanId` permits a trace tree without coupling the domain to a telemetry vendor.

### RunAggregate

A durable hourly metric bucket keyed by hour, component, and model. It stores terminal-run counts by status, input/output tokens, cost, total/min/max duration, and the first/last run timestamps represented by that bucket.

Component name and environment are snapshotted into the aggregate so historical reporting does not depend on later registration metadata changes. `ComponentId` remains the durable grouping identity.

## Transport

The initial HTTP API exists to make the first end-to-end slice usable immediately. It is not intended to become a custom observability standard.

The planned primary ingestion path is OpenTelemetry/OTLP. Incoming OTLP traces and GenAI semantic-convention attributes will be mapped into the same component/run/span domain.

## Realtime UI

The control plane uses SignalR as an in-process realtime notification channel. Persistence remains authoritative: a realtime event tells a browser that a run started or changed, then the browser re-queries the filtered run slice instead of reconstructing server query semantics client-side.

The Runs history uses keyset pagination. The latest page can refresh automatically when matching runs change. Older pages remain stable; matching activity is surfaced as an update notification that can return the operator to the latest slice.

A distributed message bus or SignalR backplane is intentionally deferred until Monitor itself needs multiple web nodes. The domain should not depend on the chosen realtime transport.

## Persistence

SQL Server is the current persistence provider, with `(localdb)\MSSQLLocalDB` as the default development instance. `MonitorDbContext` remains isolated in Infrastructure so persistence concerns do not leak into the monitoring domain or UI contract.

## Retention and aggregation

Retention distinguishes normal successful telemetry from operational evidence.

The background retention worker first aggregates terminal runs into hourly `RunAggregate` buckets. Aggregation and setting `AgentRun.AggregatedAt` happen atomically. Re-running a sweep therefore does not double-count an already aggregated run.

Only `Success` is purge-eligible. A successful run can be deleted only when both conditions are true:

1. it has already been aggregated (`AggregatedAt != null`), and
2. its completion timestamp is older than the configured successful-run detail window.

Deleting a successful run cascades to its trace spans, which is where much of the raw telemetry volume is expected to live.

`Failed` and `Cancelled` runs also contribute to aggregate metrics, but their raw records are forensic evidence and are not purged by the retention worker. Their error/failure reason, input/output payloads, and trace spans remain available for later inspection.

The worker uses a SQL Server application lock (`Monitor.RetentionAggregation`) so only one Monitor node can execute a retention sweep at a time. This prevents concurrent nodes from selecting and aggregating the same unmarked runs.

The default policy is intentionally conservative:

- aggregation delay: 5 minutes;
- successful raw-detail retention: 30 days;
- sweep interval: 15 minutes;
- failed/cancelled raw-detail retention: indefinite.

Changing the successful retention window affects future purge eligibility only. Aggregate buckets are not rewritten. Disabling the worker leaves raw data untouched.

The `/usage` page combines durable aggregate totals with only terminal runs whose `AggregatedAt` is still null. This prevents double-counting during the period where aggregated successful runs are intentionally retained in raw form.

Longer-term archival tiers or aggregate rollups (hourly → daily/monthly) can be added when actual volume makes them useful; they must preserve the same forensic invariant for failed and cancelled runs.

## Control plane

Commands are intentionally absent from the first slice. Observability must be trustworthy before Monitor is allowed to alter remote workloads. Future commands should be auditable entities with requested/accepted/completed states rather than fire-and-forget HTTP actions.
