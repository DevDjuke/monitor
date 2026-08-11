# Monitor roadmap

This roadmap is the current product sequence for turning Monitor from an observability backend into an operational control plane. It is intentionally ordered: later control-plane features depend on trustworthy identity, telemetry, alerting, and audit primitives from earlier slices.

## Current foundation — complete

- Component registry, heartbeats, runs, spans, costs, tokens, and run history.
- SQL Server persistence and versioned EF Core migrations.
- Monitor-native ingestion plus OTLP/HTTP protobuf trace and log ingestion.
- Structured component/run log events with trace/span correlation and filtered log search.
- SignalR-backed live run updates.
- Durable usage aggregation and success-only retention with failed/cancelled forensic preservation.
- Failure fingerprint/category grouping and failure drill-down.
- Threshold/window/cooldown failure alerting with duplicate-evidence suppression.
- Durable alert-delivery outbox with HMAC-signed webhook delivery, retry, and dead-letter handling.
- Durable append-only operator audit trail with safe before/after snapshots and searchable history.
- Server-side filters on Runs, Logs, Usage, Alerts, and Audit.

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

## Then

### 5. Budgets and usage policy

- Daily/monthly cost and token budgets per component/environment/model.
- Warning and critical thresholds.
- Budget alert rules integrated with the existing alert/outbox pipeline.
- Later: optional enforcement hooks once control-plane commands exist.

### 6. Component control commands

- Durable command/outbox model for pause, disable, restart, kill active run, and configuration refresh.
- Agent polling or push transport with explicit acknowledgement/result.
- Idempotent command execution and timeout state.
- Full audit trail for operator commands.

## Polish and operator ergonomics

### 7. Saved views

- Save named filter combinations for Runs, Usage, Alerts, logs, and Audit.
- Personal views first; shared/team views later if Monitor becomes multi-user.
- Fast links for common operational slices such as production failures, expensive model runs, rate limits, dead letters, and security-sensitive changes.

### 8. Richer live experience

- Incremental live run/span tree rather than only list refreshes.
- Live status and duration updates for active spans.
- Streaming run events/logs now that the logs model exists.
- Clear latest-vs-historical behavior so realtime updates never destabilize forensic browsing.

## Later platform work

- Additional alert-delivery adapters: email, Slack, Teams, Discord, PagerDuty-style integrations.
- OTLP/HTTP JSON and OTLP/gRPC support.
- OTLP metrics.
- Daily/monthly aggregate rollups when real data volume makes hourly-only retention inefficient.
- Multi-node operational hardening, shared Data Protection key management, and deployment packaging.
- Roles/permissions if Monitor grows beyond a single-owner control plane.

## Security debt explicitly tracked

The current development ingestion key may remain in solution/local development configuration temporarily. Before production hardening, move ingestion keys, webhook secrets, Data Protection keys, and other operational secrets to an appropriate vault/secret store with documented rotation and recovery procedures.
