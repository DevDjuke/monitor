# Audit trail

Monitor's audit trail is the durable record of control-plane changes. It is intentionally separate from logs, traces, alert events, and delivery history: telemetry explains what workloads did; audit explains who or what changed Monitor's operational state.

## Record shape

Each `AuditEvent` contains:

- `Id`
- `OccurredAt`
- `ActorType`: `Operator`, `System`, or `Component`
- optional `ActorId` and `ActorName`
- stable `Action`
- `TargetType`, optional `TargetId`, and optional `TargetName`
- optional `BeforeJson`
- optional `AfterJson`
- optional `MetadataJson`

Action and target strings are deliberately stable identifiers such as `alert-rule.updated`, `alert-destination.disabled`, and `component-credential.rotated`. They are suitable for server-side filtering and future reporting without coupling the domain to individual Razor pages.

## Atomicity contract

An audit record must not be a best-effort side effect after a successful mutation.

`AuditTrailWriter` only stages an `AuditEvent` in the current `MonitorDbContext`; it does not call `SaveChanges`. Operator handlers stage the domain change and audit record and then call one `SaveChangesAsync`. SQL Server commits or rolls back the two together.

The permanent `audit-integration` GitHub Actions gate proves this contract. It installs a temporary SQL trigger that deliberately rejects an `alert-destination.disabled` audit insert, performs the real Razor action, and verifies that the destination remains enabled and no audit row exists. The trigger is then removed and the same action is verified to commit the mutation and audit record together.

## Append-only and target independence

`AuditEvent` has private setters and no mutation methods. Monitor exposes no edit/delete endpoint for audit records.

`AuditEvents` also has **no foreign keys to operational targets**. `TargetId` and `TargetName` are evidence snapshots. This is deliberate: deleting or retaining a rule, credential, destination, run, or future control-plane entity must not cascade away the history that says it was changed.

The telemetry retention worker does not aggregate or purge audit rows. An explicit future audit archival policy can be introduced separately if required, but it must preserve the evidentiary semantics rather than inheriting run/log retention rules.

## Secret handling

Audit snapshots are allow-listed. Monitor does not serialize arbitrary tracked entities into audit JSON.

The following must never be stored in `BeforeJson`, `AfterJson`, or `MetadataJson`:

- component credential plaintext tokens;
- component credential SHA-256 hashes;
- webhook `ProtectedSecret` values;
- raw webhook signing secrets.

Credential snapshots include the public `KeyId`, name, component id, lifecycle timestamps, and actor metadata. Destination snapshots include name, kind, endpoint URL, enabled state, and delivery-health metadata.

The audit integration gate explicitly searches stored audit JSON for a known webhook test secret, `ProtectedSecret`, and `KeyHash` and fails if any are present.

## Current operator coverage

Monitor currently records these operator actions:

- `alert.acknowledged`
- `alert-rule.created`
- `alert-rule.updated`
- `alert-rule.enabled`
- `alert-rule.disabled`
- `alert-rule.deleted`
- `alert-destination.created`
- `alert-destination.enabled`
- `alert-destination.disabled`
- `alert-destination.tested`
- `alert-delivery.requeued`
- `component-credential.issued`
- `component-credential.rotated`
- `component-credential.revoked`

Existing background telemetry processing is not duplicated into the audit stream merely because Monitor performed it. `AuditActorType.System` and `AuditTrailWriter.RecordSystem` exist for future automated **control-plane mutations** where an audit record is appropriate.

## Operator UI

`/audit` is authenticated and supports server-side filtering by:

- time window;
- actor type;
- actor name/id;
- action;
- target type;
- target id;
- free text across actor/action/target and stored structured snapshots;
- result limit of 50, 100, 200, or 500 rows.

Rows are newest-first and expose expandable, pretty-printed before/after/metadata JSON.

## Extending the audit trail

When adding a new operator or automated control-plane mutation:

1. define or reuse a stable action constant;
2. capture the safe pre-mutation state before changing the entity;
3. mutate the entity;
4. stage the audit record through `AuditTrailWriter` with safe after/metadata snapshots;
5. commit both with the same `SaveChangesAsync` boundary;
6. add integration coverage for security-sensitive or high-impact actions.

Do not call `SaveChanges` inside the writer and do not pass secrets or whole persistence entities into audit snapshots.
