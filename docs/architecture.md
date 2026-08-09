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

### TraceSpan

One nested operation inside a run. Spans can represent agent reasoning steps, model calls, tool calls, HTTP calls, or ordinary internal work. `ParentSpanId` permits a trace tree without coupling the domain to a telemetry vendor.

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

Retention must distinguish successful telemetry from failure evidence.

Successful runs may later be aggregated into durable metrics and then have their raw run/span payloads compacted or removed according to retention policy. Aggregates can preserve counts, success rates, latency distributions, token usage, costs, model/component dimensions, and other reporting data without retaining every successful trace forever.

Failed runs follow a stricter invariant: they may also contribute to aggregates, but their full inspectable data must remain available for later investigation. That includes the run record, error/failure reason, relevant input/output payloads, and trace spans. Retention work must not replace failed-run forensic detail with aggregates alone.

The exact retention windows, archival tiers, and storage limits are deliberately deferred until real workload volume makes those trade-offs measurable.

## Control plane

Commands are intentionally absent from the first slice. Observability must be trustworthy before Monitor is allowed to alter remote workloads. Future commands should be auditable entities with requested/accepted/completed states rather than fire-and-forget HTTP actions.
