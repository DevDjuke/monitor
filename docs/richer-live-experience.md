# Richer live experience

P8 turns Monitor's existing SignalR connection into a live operational drill-down without sacrificing deterministic forensic browsing.

## Core rule: realtime is an invalidation channel

SignalR notifications are not the source of truth. SQL Server remains authoritative and `/api/runs/{id}` is the authoritative run-detail snapshot.

Detailed realtime notifications are deliberately small. They tell an authenticated browser that persisted state changed; the browser reconciles from the snapshot or patches a command row from the persisted transition payload.

This has three consequences:

1. missed SignalR messages are recoverable;
2. duplicate messages are harmless;
3. a browser never treats an uncommitted mutation as fact.

## Persisted-event boundary

`MonitorRealtimeSaveChangesInterceptor` observes added or modified:

- `AgentRun`;
- `TraceSpan`;
- run-linked `LogEvent`;
- `ComponentCommand`.

It captures immutable realtime payloads before `SaveChanges`, but publishes them only from the successful post-save callback. A failed transaction clears the captured changes and emits nothing.

The interceptor does not query the same `MonitorDbContext` from `SavedChanges`. This avoids save-pipeline re-entrancy and means native ingestion, OTLP ingestion, operator actions, component acknowledgements, and background command expiry all share the same persisted-event boundary.

Realtime publication after a successful SQL commit is deliberately best-effort. If SignalR publication fails, Monitor logs the failure but does not turn an already committed mutation into an HTTP/application failure. Connected clients recover through the same authoritative snapshot/reconnect path used for missed events.

The older coarse `RunChanged` event remains for the `/runs` list. It is intentionally separate from P8's run-detail invalidations.

## SignalR groups

The authenticated `/hubs/monitor` hub exposes explicit subscriptions:

- `WatchRun(runId)` / `UnwatchRun(runId)` -> `run:{runId}` group;
- `WatchCommands()` / `UnwatchCommands()` -> command-activity group.

Run-detail updates are therefore sent only to clients currently inspecting that run rather than broadcast globally.

## Running run drill-down

A run opened while `Running` starts in **live** mode.

The browser:

- subscribes to that run's SignalR group;
- reconciles the authoritative `/api/runs/{id}` snapshot after run/span/log invalidations;
- merges spans and log events in timestamp order;
- reconciles trace rows by stable span id rather than replacing the page;
- derives trace indentation from `ParentSpanId`;
- updates active run/span durations locally every 500 ms;
- performs a low-frequency safety reconciliation while visible;
- reconciles immediately after the tab becomes visible again.

Telemetry text and structured JSON are inserted with DOM `textContent`, not raw HTML.

When the authoritative run status becomes terminal, the final snapshot is applied and the page switches to frozen mode.

## Historical and terminal runs

A run opened in `Success`, `Failed`, or `Cancelled` state is **historical · frozen**.

Late OTLP spans, correlated logs, failure-group changes, or other persisted telemetry do not silently rewrite the page underneath an operator. Instead Monitor displays an explicit update banner. The operator may choose to refresh that frozen snapshot.

This is the key forensic invariant of P8: realtime must not make an already inspected historical view drift without the operator noticing.

## Reconnect behavior

Automatic SignalR reconnect cannot prove that no events were missed while disconnected.

Therefore:

- live running views re-subscribe and immediately reconcile from the authoritative snapshot;
- frozen run views remain frozen and display a refresh prompt;
- command views re-subscribe and display a reconciliation prompt.

The UI always converges through persisted state rather than assuming the event stream was lossless.

## Command transitions

Both `/commands` and the recent command history on `/components/{id}` subscribe to command activity.

For a command row already visible in a live view, Monitor updates in place:

- status;
- delivery-attempt count;
- current lease expiry;
- result/error/completion;
- cancellation action availability.

The row is briefly highlighted so the state change is visible.

Monitor deliberately does **not** silently insert, remove, or reorder rows when that could change the meaning of the operator's current view:

- a newly matching command raises a refresh banner;
- a transition that no longer matches the selected status filter leaves the row visible but marks it as filter-mismatched and raises a refresh banner;
- `Window=all` command history is treated as frozen historical browsing and only raises update banners;
- when a free-text filter is active and a newly published command lacks enough denormalized text to prove whether it matches, Monitor conservatively raises a refresh prompt instead of risking a false negative.

This mirrors the existing `/runs` latest-vs-older-page contract.

## Snapshot surface

`GET /api/runs/{id}` now contains the complete data needed for live reconciliation:

- run state, timing, model, token use, cost, payloads, error, and minimal failure identity;
- ordered spans with parent identity, timing, status, diagnostics, attributes, and OTLP ids;
- ordered structured logs with run/span correlation, severity, message/template, properties, exception details, source, and OTLP ids.

The endpoint keeps the existing ingestion-credential/operator authorization boundary. Because it loads both span and log collections repeatedly while a run is live, EF Core uses split-query loading for this snapshot to avoid multiplying the two child collections into a large Cartesian result set.

## Deliberate non-goals

P8 does not add a new database schema or replace SQL-backed history with an event-stream database. It also does not make every dashboard widget live. The live contract is focused on the operational surfaces where timing matters most: active run drill-down and component command execution.

Future work may add live alert/budget counters or multi-node SignalR scale-out if real deployment requirements justify it.
