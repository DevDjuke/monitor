# Monitor roadmap

This roadmap is the current product sequence for turning Monitor from an observability backend into an operational control plane. It is intentionally ordered: later control-plane features depend on trustworthy identity, telemetry, alerting, and audit primitives from earlier slices.

## Current foundation — complete

- Component registry, heartbeats, runs, spans, costs, tokens, and run history.
- SQL Server persistence and versioned EF Core migrations.
- Monitor-native ingestion plus OTLP/HTTP protobuf trace ingestion.
- SignalR-backed live run updates.
- Durable usage aggregation and success-only retention with failed/cancelled forensic preservation.
- Failure fingerprint/category grouping and failure drill-down.
- Threshold/window/cooldown failure alerting with duplicate-evidence suppression.
- Durable alert-delivery outbox with HMAC-signed webhook delivery, retry, and dead-letter handling.
- Server-side filters on Runs, Usage, and Alerts.

## Next

### 1. Alert rule management

Status: **in progress**

- Create alert rules from `/alerts` or a failure fingerprint.
- Edit rule name, threshold, rolling window, cooldown, and enabled state.
- Assign delivery destinations per rule, while retaining an explicit "all enabled destinations" mode.
- Safely remove rules without destroying historical alert evidence.
- Keep rule configuration visible and manageable from the Alerts control surface.

### 2. Per-component ingestion credentials

- Issue ingestion keys to individual monitored components/exporters.
- Store only protected/hashed credential material where possible.
- Last-used timestamps and credential metadata.
- Rotate/revoke credentials without changing unrelated components.
- Preserve a controlled bootstrap/admin ingestion path for setup and migration.
- Move shared/local development secrets from repository configuration to a proper vault/secret store.

### 3. Logs and run events

- Structured log/event records linked to runs and optionally spans.
- Level, timestamp, message template, properties, exception data, and source.
- Search/filter by component, level, run, span, environment, and text.
- OTLP log ingestion after the internal model is stable.
- Inline event timeline on run drill-down.

## Then

### 4. Audit trail

- Durable actor/action/target records for operator and system changes.
- Acknowledge, rule edits, destination edits, credential rotation, control commands, and security-sensitive configuration changes.
- Before/after metadata where appropriate.
- Searchable audit UI.

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

- Save named filter combinations for Runs, Usage, Alerts, and logs.
- Personal views first; shared/team views later if Monitor becomes multi-user.
- Fast links for common operational slices such as production failures, expensive model runs, rate limits, and dead letters.

### 8. Richer live experience

- Incremental live run/span tree rather than only list refreshes.
- Live status and duration updates for active spans.
- Streaming run events/logs when the logs model exists.
- Clear latest-vs-historical behavior so realtime updates never destabilize forensic browsing.

## Later platform work

- Additional alert-delivery adapters: email, Slack, Teams, Discord, PagerDuty-style integrations.
- OTLP/HTTP JSON and OTLP/gRPC support.
- OTLP metrics and logs.
- Daily/monthly aggregate rollups when real data volume makes hourly-only retention inefficient.
- Multi-node operational hardening, shared Data Protection key management, and deployment packaging.
- Roles/permissions if Monitor grows beyond a single-owner control plane.

## Security debt explicitly tracked

The current development ingestion key may remain in solution/local development configuration temporarily. Before production hardening, move ingestion keys, webhook secrets, Data Protection keys, and other operational secrets to an appropriate vault/secret store with documented rotation and recovery procedures.
