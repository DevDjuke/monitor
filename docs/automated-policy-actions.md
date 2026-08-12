# Automated policy actions

P11 adds opt-in control actions to usage budgets without creating a second control plane. A budget can still remain alert-only, or it can enqueue one ordinary durable component command when that budget first crosses `Critical` in a UTC budget period.

## Supported actions

The first enforcement version intentionally supports only:

```text
None
Pause
Disable
```

`None` is the default. `Pause` and `Disable` require the budget to be scoped to one concrete component. Global, environment-only, and model-only budgets remain alert-only because Monitor must never guess which workload a broad budget should control.

There is no automatic `Resume` or `Enable`. Recovery is an explicit operator decision.

## Persistence

Active enforcement configuration is stored in the versioned `UsageBudgetEnforcementPolicies` table:

```text
UsageBudgetId  PK/FK -> UsageBudgets.Id
CriticalAction
UpdatedAt
```

Only active `Pause` or `Disable` choices have a row. `None` is represented by absence of a row.

The table is a deliberately small policy sidecar accessed through `UsageBudgetEnforcementPolicyStore`. It is created by an EF Core migration but is not mapped into the main EF aggregate model. This keeps the existing `UsageBudget` aggregate and model snapshot unchanged while still making the policy configuration durable and transaction-capable.

Budgets are currently soft-deleted, so their enforcement-policy row remains durable with the budget record. If a budget is physically removed in the future, the database foreign key cascades the sidecar row.

## Evaluation and exact-once policy intent

Budget accounting and alert evaluation remain unchanged. The existing `Monitor.UsageBudgets` SQL Server application lock still serializes budget evaluation.

Within one UTC daily/monthly period, `UsageBudget.LastTriggeredLevel` allows at most one Warning and one Critical threshold transition. P11 reuses that existing state as its policy-action deduplication boundary.

When a Critical crossing occurs, one evaluator transaction stages:

1. the `UsageBudgetAlertEvent`;
2. any alert-delivery outbox rows;
3. the optional `ComponentCommand`;
4. the system audit records;
5. the budget's `LastTriggeredLevel = Critical` state.

`SaveChanges` commits those records atomically. A failed transaction therefore cannot leave a command without its Critical evidence, or mark the threshold as handled without persisting the command.

Repeated sweeps over the same Critical evidence do not enqueue a second command. A new UTC budget period resets threshold state and may produce a new action if the new period crosses Critical.

Changing or enabling an action after a budget has already reached Critical does not retroactively enqueue another command in the same period. The new configuration applies to the next eligible Critical transition. An operator can issue an ordinary command manually if immediate intervention is required.

## Command contract

Policy enforcement uses the existing `ComponentCommand` table and protocol. There is no policy-specific command queue.

Mappings are:

```text
Critical -> Pause    => ComponentCommandType.Pause
Critical -> Disable  => ComponentCommandType.Disable
```

Generated commands use:

```text
RequestedBy = policy:usage-budget
```

The command JSON payload carries safe provenance:

```json
{
  "source": "usage-budget",
  "budgetId": "...",
  "budgetName": "...",
  "alertEventId": "...",
  "level": "Critical",
  "action": "Pause",
  "periodStart": "...",
  "periodEnd": "...",
  "utilizationPercent": 112.4
}
```

After enqueue, the command behaves exactly like an operator-issued command:

- component polling and scope authorization;
- leasing and lease-token acknowledgement;
- redelivery after lease loss;
- command expiry/max-attempt behavior;
- idempotency through the command id;
- realtime command-state updates;
- component-side success/failure/rejection acknowledgement;
- server-side application of successful Pause/Disable state.

The budget evaluator never directly mutates `MonitoredComponent.ControlState` or `Enabled`.

## Audit

The Critical threshold keeps its existing system audit event:

```text
usage-budget.critical
```

A generated policy command additionally writes the ordinary command issuance action:

```text
component-command.issued
```

with system actor `UsageBudgetEvaluator` and metadata containing the budget id, alert event id, component id, action, period, and utilization. The command then produces the same component success/failure/rejection audit events as every other command.

Operator changes to the configured action are captured in the existing `usage-budget.created` / `usage-budget.updated` audit snapshots.

## Failure and safety behavior

The operator UI prevents Pause/Disable enforcement unless a concrete component is selected. The evaluator defensively refuses to issue a command if invalid persisted configuration is encountered and logs the configuration error; the Critical alert itself still remains valid evidence.

P11 does not:

- auto-resume or auto-enable;
- choose a different action when a command fails or expires;
- retry by creating a new command in the same budget period;
- apply actions to several components from a broad budget;
- mutate a workload directly from the evaluator;
- add failure-rule actions yet.

These constraints keep policy automation bounded and auditable.

## Integration gate

`.github/workflows/policy-actions-ci.yml` runs against SQL Server and the real Monitor application. It verifies:

- the new migration;
- rejection of ambiguous global enforcement;
- persistence of Pause and Disable policy configuration;
- real Critical crossings for both action kinds;
- one and only one policy command per budget/period/threshold despite repeated sweeps;
- command payload provenance and system audit evidence;
- normal command claim and acknowledgement through the component API;
- successful Pause and Disable state application;
- absence of policy-generated Resume/Enable recovery commands.
