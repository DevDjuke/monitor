# Monitor roadmap

This roadmap is the current product sequence for turning Monitor from an observability backend into an operational control plane. It is intentionally ordered: later control-plane features depend on trustworthy identity, telemetry, alerting, and audit primitives from earlier slices.

## Current foundation — complete

- Component registry, heartbeats, runs, spans, costs, tokens, and run history.
- SQL Server persistence and versioned EF Core migrations.
- Monitor-native ingestion plus OTLP/HTTP protobuf trace and log ingestion.
- Structured component/run log events with trace/span correlation and filtered log search.
- SignalR-backed live run lists, run drill-down, log/span reconciliation, and command-state transitions with explicit frozen-history behavior.
- Durable usage aggregation and success-only retention with failed/cancelled forensic preservation.
- Failure fingerprint/category grouping and failure drill-down.
- Threshold/window/cooldown failure alerting with duplicate-evidence suppression.
- Durable alert-delivery outbox with HMAC-signed webhook delivery, retry, and dead-letter handling.
- Durable append-only operator audit trail with safe before/after snapshots and searchable history.
- Daily/monthly cost and token budget policy with warning/critical alert delivery.
- Durable leased component control commands with acknowledgement, redelivery, timeout/expiry, and audit history.
- Personal saved operational views with canonical filter persistence and pinned sidebar shortcuts.
- Server-side filters on Runs, Logs, Usage, Alerts, Budgets, Commands, and Audit.

## Next

### 1. Alert rule management

Status: **complete**

- Create alert rules from `/alerts/rules` or directly from a failure fingerprint.
- Edit rule name, threshold, rolling window, cooldown, and enabled state.
- Assign delivery destinations per rule, while retaining an explicit "all enabled destinations" mode.
- Soft-delete rules without destroying historical alert events or delivery evidence.
- Keep rule configuration visible and manageable from the Alerts control surface.

### 2. Per-component ingestion credentials

Status: **complete**

- Issue independent ingestion keys from each component's detail page and show plaintext only at issue/rotation time.
- Store only a random public key id plus the SHA-256 hash of the complete token; no plaintext secret is persisted.
- Track creation/revocation actors and timestamps plus write-throttled last-used metadata.
- Rotate or revoke one component credential without changing unrelated components.
- Enforce component scope across Monitor-native registration, heartbeat, run/span access, queries, and OTLP ingestion.
- Reject valid component credentials with HTTP 403 when they target another component; revoked/invalid credentials return 401.
- Preserve the shared `Monitor__IngestionApiKey` as a controlled bootstrap/migration path while deployments move to scoped keys.
- Detailed security and migration contract: `docs/component-ingestion-credentials.md`.
- Move shared/local development secrets from repository configuration to a proper vault/secret store remains explicit security debt.

### 3. Logs and run events

Status: **complete**

- Persist structured log/event records at component scope with optional run and span correlation.
- Capture level, event timestamp/observed time, event name, message/template, JSON properties, exception type/message/stack, and instrumentation source.
- Provide Monitor-native run-event ingestion through `Monitor.Client` plus standard OTLP/HTTP protobuf logs at `/v1/logs`.
- Apply the same per-component ingestion credential scope to OTLP logs as traces.
- Correlate OTLP trace/span ids immediately when possible and backfill logs that arrive before their trace.
- Deduplicate OTLP retry payloads without discarding distinct real log occurrences.
- Search/filter `/logs` by time, component, environment, minimum level, source, run, span, and text.
- Merge spans and structured events into a timestamp-ordered timeline on run drill-down.
- Cascade run-linked logs with successful raw-run retention; retain failed/cancelled run-linked log evidence with the forensic run, and expire unlinked/component-only logs on their own bounded retention window.

### 4. Audit trail

Status: **complete**

- Persist append-only `AuditEvent` records with occurred time, actor type/id/name, action, target type/id/name, and optional before/after/metadata JSON.
- Stage the audit row in the same EF Core `SaveChanges` boundary as the operator mutation so a change cannot commit without its corresponding audit evidence.
- Audit alert acknowledgement, alert-rule create/edit/toggle/delete, webhook destination create/toggle/test, alert-delivery requeue, and component credential issue/rotate/revoke.
- Keep snapshots deliberately secret-safe: component credential plaintext/hash and protected webhook signing secrets are never copied into audit JSON.
- Keep audit evidence independent of mutable targets: `AuditEvents` has no foreign keys to operational tables, so later deletion or retention cannot cascade away history.
- Support operator/system/component actor types and provide a system-writer path for future automated control-plane changes.
- Search/filter `/audit` by time window, actor, action, target, target id, and free text, with expandable before/after/metadata snapshots.
- Retain audit history independently of telemetry retention; the current retention worker does not purge audit records.

### 5. Budgets and usage policy

Status: **complete**

- Configure daily or monthly budgets with optional component, environment, and model scope; an empty scope acts as a global budget.
- Limit reported cost, total tokens, or both and define separate warning/critical utilization percentages.
- Evaluate budgets from the same accounting contract as `/usage`: durable hourly aggregates plus only terminal raw runs that have not yet been aggregated.
- Emit Warning once and Critical once per UTC budget period; a new period resets notification state without deleting historical events.
- Assign delivery destinations per budget or use all enabled destinations.
- Deliver budget alerts through the existing webhook destination, HMAC signing, retry/backoff, health and dead-letter infrastructure under the shared dispatcher lock.
- Manage policies, threshold history, acknowledgement and budget delivery state from `/budgets`, including manual retry for non-delivered budget notifications.
- Audit budget create/edit/enable/disable/delete, acknowledgement/requeue, plus system-originated warning/critical threshold crossings.
- Detailed contract: `docs/budgets-and-usage-policy.md`.
- Enforcement remains intentionally absent until component control commands exist; P5 is detection/notification policy only.

### 6. Component control commands

Status: **complete**

- Persist commands as durable operational records with `Pending`, `Leased`, `Succeeded`, `Failed`, `Rejected`, `Cancelled`, and `Expired` states.
- Support `Pause`/`Resume`, `Disable`/`Enable`, `Restart`, `KillRun`, and `RefreshConfiguration` commands.
- Deliver commands through component polling with a short lease; the command id is the idempotency key and each delivery attempt receives a fresh lease token.
- Redeliver an unacknowledged command after lease expiry while rejecting acknowledgements carrying a superseded lease token.
- Serialize claim, completion, cancellation, and expiry per component with a SQL Server application lock.
- Keep workload control separate from credential admission: Pause/Disable block new runs while heartbeat, existing-run telemetry, and command polling remain available.
- Enforce the control state server-side when a new run is started, returning HTTP 409 rather than relying only on cooperative agent behavior.
- Keep `TargetRunId` as forensic command data rather than an FK to Runs so successful-run retention cannot erase or block command history.
- Audit operator issuance/cancellation, component success/failure/rejection, and system expiry with command payloads excluded from immutable audit snapshots.
- Provide a dedicated `MonitorControlClient`, per-component command UI, and central filtered `/commands` history.
- Dogfood the protocol in `Monitor.SampleWorker`; restart remains host-specific and is explicitly rejected unless a process supervisor integration exists.
- Detailed contract: `docs/component-control-commands.md`.

## Polish and operator ergonomics

### 7. Saved views

Status: **complete**

- Save named filter combinations for Runs, Logs, Usage, Alerts, Budgets, Audit, and Commands.
- Scope every view to the authenticated ASP.NET Core Identity user; another user's saved view cannot be listed or mutated even when its id is known.
- Persist a canonical, per-surface allow-listed query string instead of duplicating each page's filter schema into preference tables.
- Make Runs filter state URL-addressable while keeping keyset pagination cursor state transient and excluded from saved views.
- Apply, rename, pin/unpin, and delete views from `/saved-views`; create views directly from supported operational pages through one reusable toolbar.
- Allow up to 100 personal views and six pinned sidebar fast links per user.
- Keep personal workspace preferences out of the immutable operational `AuditEvent` stream.
- Shared/team views remain deferred until Monitor has an explicit multi-user role/permission model.
- Detailed contract: `docs/saved-views.md`.

### 8. Richer live experience

Status: **complete**

- Subscribe run drill-down clients to authenticated per-run SignalR groups and reconcile persisted run/span/log changes against the authoritative `/api/runs/{id}` snapshot.
- Reconcile the trace tree incrementally by stable span id and parent id instead of refreshing the whole page.
- Update durations locally for running runs and spans while authoritative status/timing still comes from persisted telemetry.
- Stream both Monitor-native and OTLP-backed run events/logs through one EF Core post-save invalidation boundary.
- Publish detailed realtime invalidations only after a successful `SaveChanges`; failed transactions emit no phantom state and the interceptor never re-enters the same DbContext from the post-save callback.
- Keep active runs in auto-follow mode, then freeze the view when the run becomes terminal.
- Treat terminal runs as historical snapshots: late telemetry raises an explicit update banner instead of silently rewriting forensic evidence under the operator.
- Reconcile active views after SignalR reconnect/visibility restore because realtime delivery is not treated as lossless.
- Stream command transitions on both `/commands` and component detail history, updating visible rows in place without silently inserting/reordering/removing rows that would change a filtered view.
- Treat `Window=all` command history as frozen and prompt for refresh on new activity or after reconnect.
- Keep the existing coarse `RunChanged` event for the latest Runs list while P8 detailed events are group-scoped.
- Detailed contract: `docs/richer-live-experience.md`.

## Later platform work

- Additional alert-delivery adapters: email, Slack, Teams, Discord, PagerDuty-style integrations.
- Host/process-supervisor command adapters for restart semantics where appropriate.
- Optional budget enforcement policies that enqueue control commands rather than mutating workloads directly.
- OTLP/HTTP JSON and OTLP/gRPC support.
- OTLP metrics.
- Daily/monthly aggregate rollups when real data volume makes hourly-only retention inefficient.
- Multi-node operational hardening, shared Data Protection key management, and deployment packaging.
- Roles/permissions if Monitor grows beyond a single-owner control plane.

## Security debt explicitly tracked

The current development ingestion key may remain in solution/local development configuration temporarily. Before production hardening, move ingestion keys, webhook secrets, Data Protection keys, and other operational secrets to an appropriate vault/secret store with documented rotation and recovery procedures.
