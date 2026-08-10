# Logs and run events

Monitor persists structured operational events in the same component/run/span model used for traces. The internal model is deliberately transport-neutral: Monitor-native clients and standard OpenTelemetry log exporters both produce `LogEvent` records.

## LogEvent model

A log event always belongs to a `MonitoredComponent` and can optionally correlate to an `AgentRun` and `TraceSpan`.

Stored fields include:

- event timestamp and observed timestamp;
- normalized `LogEventLevel` plus original OTLP severity text;
- event name;
- formatted message and message template;
- structured properties as JSON;
- exception type, message, and stack trace;
- instrumentation/source name;
- OTLP trace and span ids when present;
- optional external log-record id and a Monitor dedupe key.

Logs are allowed without trace correlation. This is important for service startup/shutdown messages and for OTLP logs that arrive before their corresponding trace export.

## Monitor-native ingestion

A run can accept structured events at:

```text
POST /api/runs/{runId}/events
X-Monitor-Key: <bootstrap or component credential>
Content-Type: application/json
```

Example:

```json
{
  "level": "Warning",
  "message": "Queue depth is 42",
  "messageTemplate": "Queue depth is {Depth}",
  "propertiesJson": "{\"Depth\":42,\"Queue\":\"invoices\"}",
  "source": "Invoice.Worker",
  "eventName": "queue.depth.warning"
}
```

Component credentials use the same ownership rules as runs and spans: a credential for component A receives HTTP 403 when it tries to write an event to component B's run.

`Monitor.Client` exposes both the complete structured record and a convenience helper:

```csharp
await run.RecordEventAsync(new LogEventRecord(
    LogEventLevel.Warning,
    "Queue depth is 42",
    MessageTemplate: "Queue depth is {Depth}",
    Properties: new { Depth = 42, Queue = "invoices" },
    Source: "Invoice.Worker",
    EventName: "queue.depth.warning"));

await run.LogAsync(
    LogEventLevel.Information,
    "Invoice batch completed.",
    new { Count = 14 },
    source: "Invoice.Worker");
```

## OpenTelemetry / OTLP logs

Monitor accepts the stable OTLP log export protobuf contract at:

```text
POST /v1/logs
Content-Type: application/x-protobuf
X-Monitor-Key: <bootstrap or component credential>
```

`application/protobuf` and optional `Content-Encoding: gzip` are also accepted. OTLP/HTTP JSON and OTLP/gRPC remain future transports.

The mapping is:

```text
OpenTelemetry Resource          -> MonitoredComponent
service.name                    -> component name
service.namespace + name        -> component slug
deployment.environment.name     -> environment
service.version                 -> version
InstrumentationScope.name       -> LogEvent.Source
LogRecord.time_unix_nano        -> Timestamp
LogRecord.observed_time_unix... -> ObservedAt
LogRecord.severity_number       -> LogEventLevel
LogRecord.severity_text         -> SeverityText
LogRecord.body                  -> Message
LogRecord.event_name            -> EventName
LogRecord.attributes            -> PropertiesJson
OriginalFormat/message.template -> MessageTemplate
exception.type                  -> ExceptionType
exception.message               -> ExceptionMessage
exception.stacktrace            -> ExceptionStackTrace
trace_id                        -> AgentRun correlation
span_id                         -> TraceSpan correlation
```

A component-scoped credential must match the OTLP resource's component slug and environment. A valid credential for a different component is rejected before any records in that request are imported.

## Correlation and ordering

When the matching trace already exists, OTLP logs resolve `trace_id` to the run and `span_id` to the internal span during import. If the log arrives first, Monitor keeps its external correlation ids and `LogCorrelationWorker` periodically backfills the relationship after the trace appears.

Run drill-down merges spans and log events into one timestamp-ordered timeline. The original trace tree is still shown separately, so the timeline does not replace span hierarchy.

## Retry deduplication

OTLP exporters may retry an identical export after a network failure. Monitor computes a deterministic SHA-256 dedupe identity for OTLP records under a SQL Server application lock.

When `log.record.uid` exists, that stable external record id is preferred. Otherwise the identity uses component, instrumentation source, trace/span ids, event/severity, OTLP timestamps, message, and structured properties. This suppresses retransmission of the same record without collapsing distinct real occurrences that merely have the same message text.

Monitor-native events are not message-deduplicated; every explicit API call represents a distinct event.

## Logs control plane

The authenticated `/logs` page supports server-side, query-string filters for:

- time window;
- component;
- environment;
- minimum severity;
- instrumentation/source;
- run id;
- span id;
- free-text search across messages, templates, exception fields, source/event name, and structured properties;
- result size up to 500 rows.

The page also shows matching/error/warning/unlinked counts. Structured properties and exception detail remain expandable rather than flattening everything into message text.

## Retention

Run-linked logs follow the raw-run retention contract:

- logs linked to successful runs are deleted by cascade when the already-aggregated successful raw run reaches its detail-retention cutoff;
- logs linked to failed or cancelled runs remain because those forensic runs are retained indefinitely;
- component-only or not-yet-correlated logs cannot inherit a run outcome, so they have their own bounded `Retention:UnlinkedLogDetailDays` window (30 days by default).

This prevents ordinary uncorrelated logging from growing without bound while preserving the existing failure-forensics invariant.

## Current transport boundary

Implemented now:

- Monitor-native structured run events;
- OTLP/HTTP protobuf traces;
- OTLP/HTTP protobuf logs.

Not claimed yet:

- OTLP/HTTP JSON;
- OTLP/gRPC;
- OTLP metrics;
- live SignalR streaming of individual log events.

Live event streaming is intentionally deferred to the richer-live-UI roadmap slice; persisted state remains authoritative.
