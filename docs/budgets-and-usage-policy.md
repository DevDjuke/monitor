# Budgets and usage policy

Monitor budgets are detection and notification policies over the same usage accounting shown by `/usage`. P11 additionally allows a component-scoped budget to opt into one bounded control action when it first crosses `Critical` in a UTC budget period.

## Scope

A budget may constrain any combination of:

- component;
- environment;
- model.

Leaving all three empty creates a global budget. A component is already environment-specific in Monitor, but environment remains independently useful for policies that span all components in one deployment environment.

Budgets are either `Daily` or `Monthly`. Period boundaries are UTC:

- daily: `00:00:00Z` to the next UTC day;
- monthly: first day `00:00:00Z` to the first day of the next month.

## Accounting invariant

Budget evaluation must not double-count retained successful raw runs after they have already contributed to a `RunAggregate`.

For the active budget period Monitor therefore sums:

1. matching durable hourly `RunAggregate` rows; plus
2. matching terminal raw `AgentRun` rows where `AggregatedAt == null`.

Running work is not included until it has terminal reported token/cost usage. Failed and cancelled terminal runs are included in usage just like successful runs because cost/tokens were still consumed.

This is the same aggregate-plus-pending-raw rule used by `/usage`.

## Limits and thresholds

A budget requires a positive cost limit, token limit, or both. If both are configured, Monitor calculates both utilization percentages and uses the larger percentage as the budget utilization.

Example:

```text
Cost:   $85 / $100 = 85%
Tokens: 700k / 1M  = 70%
Budget utilization = 85%
```

`WarningPercent` must be lower than `CriticalPercent`. Defaults are 80% and 100%.

Within one UTC budget period Monitor emits at most:

1. one Warning event when warning is first crossed;
2. one Critical event when critical is first crossed.

Repeated evaluator sweeps over unchanged evidence do not create duplicate alerts. A new UTC daily/monthly period resets the notification state for the new period while historical events remain durable.

## Delivery

A budget can use all enabled alert destinations or an explicit selected destination set.

Budget alerts use the shared durable alert-delivery infrastructure:

- the same `AlertDeliveryDestination` records;
- signed webhook, Slack, Teams, Discord, PagerDuty, and SMTP adapters;
- Data Protection protected provider secrets/configuration;
- the same retry/backoff and destination-health behavior;
- the same `Monitor.AlertDelivery` SQL application lock;
- the same shared `AlertDeliveryWorker`.

Budget events keep a dedicated durable outbox table so failure-alert history is not forced into a synthetic failure model. Each outbox has a database-level unique `(BudgetAlertEventId, DestinationId)` key.

Event types are:

```text
usage.budget.warning
usage.budget.critical
```

Payloads include the budget scope/limits, period boundaries, observed cost/tokens and utilization percentage. Delivery remains at least once and destination-specific transport contracts are documented with the alert-delivery adapters.

## Optional Critical action

`/budgets/edit` can configure one of:

```text
None
Pause
Disable
```

`None` is the default. Pause/Disable require one concrete `ComponentId`; broad budgets cannot control several workloads implicitly.

A Critical action never mutates the component directly. The evaluator creates an ordinary durable `ComponentCommand` in the same transaction as the Critical alert event and threshold state. The command is then claimed, leased, acknowledged, retried/expired, audited, and reflected through realtime updates by the existing component-control protocol.

The existing per-period `LastTriggeredLevel` state is also the action deduplication boundary, so repeated sweeps do not create duplicate commands for the same budget/period/Critical transition.

There is deliberately no automatic Resume or Enable. Recovery remains explicit operator intent.

Detailed contract: `docs/automated-policy-actions.md`.

## Operator surface

`/budgets` shows:

- enabled/warning/critical policy counts;
- current evaluator observations and utilization;
- daily/monthly scope and limits;
- configured Critical enforcement action where present;
- recent threshold events and acknowledgement state;
- budget notification outbox state, attempts, result/error and manual retry.

`/budgets/edit` manages:

- name;
- component/environment/model scope;
- daily/monthly period;
- cost/token limits;
- warning/critical percentages;
- optional Critical Pause/Disable action;
- enabled state;
- all-enabled or selected delivery destinations.

Deletion is soft. Historical budget alerts and notification evidence remain available; the enforcement-policy sidecar row is removed with the deleted budget only when the budget row itself is physically removed in the future.

## Audit

Operator actions are recorded through the existing transactional audit trail:

```text
usage-budget.created
usage-budget.updated
usage-budget.enabled
usage-budget.disabled
usage-budget.deleted
usage-budget.alert-acknowledged
usage-budget.delivery-requeued
```

Automatic threshold crossings are system audit events:

```text
usage-budget.warning
usage-budget.critical
```

P11-generated enforcement commands additionally use the ordinary:

```text
component-command.issued
```

system audit event, followed by the normal component command success/failure/rejection lifecycle events. Policy audit metadata contains safe budget/period/action provenance, not delivery secrets or credentials.

## Configuration

```json
{
  "UsageBudgets": {
    "Enabled": true,
    "InitialDelaySeconds": 10,
    "SweepIntervalSeconds": 60
  }
}
```

Environment-variable equivalents:

```text
UsageBudgets__Enabled
UsageBudgets__InitialDelaySeconds
UsageBudgets__SweepIntervalSeconds
```

The evaluator uses the SQL Server application lock `Monitor.UsageBudgets`, so only one Monitor node evaluates budget policy at a time.
