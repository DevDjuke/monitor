# Component control commands

Monitor component commands are a durable leased control protocol. They are intentionally not fire-and-forget HTTP actions and Monitor does not pretend it can directly restart or kill arbitrary remote processes.

## Model

Each `ComponentCommand` belongs to one component and stores:

- command id and component id;
- type and durable status;
- optional target run id for `KillRun`;
- optional JSON payload for `RefreshConfiguration`;
- requesting operator and creation/availability/expiry timestamps;
- current lease token, lease timestamps, and delivery-attempt count;
- terminal completion/result/error or cancellation metadata.

Supported command types are:

- `Pause` / `Resume`;
- `Disable` / `Enable`;
- `Restart`;
- `KillRun`;
- `RefreshConfiguration`.

`Restart` is a protocol command, not a promise that Monitor can restart every runtime. A component must integrate it with its actual host or supervisor (for example systemd, a Windows service, Kubernetes, or another deployment-specific mechanism). The sample worker explicitly rejects Restart because it has no such supervisor.

## State machine

```text
Pending
   |
   | claim
   v
Leased ----------------------+
   |                         |
   | explicit result         | lease expires without result
   |                         |
   +-> Succeeded             +-> eligible for re-lease
   +-> Failed                    (same command id, new lease token)
   +-> Rejected

Pending/Leased -> Cancelled     (operator)
Pending/Leased -> Expired       (absolute expiry or max delivery attempts)
```

Terminal states are `Succeeded`, `Failed`, `Rejected`, `Cancelled`, and `Expired`.

## Delivery and idempotency

The command row itself is the durable outbox. Components poll:

```text
POST /api/components/{componentId}/commands/claim
X-Monitor-Key: <component credential>
```

No available command returns HTTP 204. A claimed command returns its stable command id and a delivery-specific lease token.

A component reports a result to:

```text
POST /api/components/{componentId}/commands/{commandId}/complete
X-Monitor-Key: <component credential>
Content-Type: application/json

{
  "leaseToken": "...",
  "outcome": "Succeeded | Failed | Rejected",
  "resultJson": "optional JSON/string result",
  "error": "optional error/rejection reason"
}
```

The **command id is the execution idempotency key**. A component handler must remember or otherwise tolerate seeing the same command id more than once. Lease expiry means delivery may be retried after a worker/process/network failure.

Every delivery attempt gets a fresh lease token. Once another delivery has re-leased the command, an older token receives HTTP 409 and cannot mutate the command. Completion of an already-terminal command is idempotent: Monitor returns the existing terminal status with `alreadyTerminal = true` instead of creating another terminal transition/audit row.

Claim, completion, operator cancellation, and expiry for one component all use the same SQL Server application-lock resource:

```text
Monitor.ComponentCommands.<component-id>
```

That serializes competing state transitions across multiple Monitor web nodes.

## Workload control versus credential admission

`MonitoredComponent.Enabled` remains the registry/credential-admission switch. Component-scoped credentials are invalid when that flag is false.

Control commands use the separate `ComponentControlState`:

```text
Active
Paused
Disabled
```

`Pause` and `Disable` do **not** revoke the component credential. They block creation of new runs while still allowing:

- heartbeat;
- completion/logs/spans for work that was already running;
- command polling and acknowledgement.

This distinction is required for safe recovery. A component must be able to finish/report existing work and receive Resume/Enable after workload admission has been stopped.

Monitor enforces this on the server as well as expecting cooperative component behavior. `MonitoredComponent.MarkRunStarted` refuses new work unless the component is `Enabled` and `ControlState == Active`. The HTTP API maps that condition to HTTP 409 `component_work_blocked`.

## KillRun

A `KillRun` command may only be created for a currently running run owned by the target component.

`TargetRunId` is deliberately stored as forensic command data rather than a relational FK to `Runs`. Successful raw-run retention may later delete the run; that must not delete the command, prevent retention, or destroy the historical fact that a kill was requested.

Actual cancellation remains component/runtime-specific. The sample worker maps KillRun to the active run's cancellation token and reports that cancellation was requested; the normal run lifecycle then records the run as cancelled.

## RefreshConfiguration

Only `RefreshConfiguration` accepts `PayloadJson`. The operator UI validates that supplied content is valid JSON.

Command payloads are persisted on the command because the receiving component needs them, but they are deliberately excluded from immutable audit snapshots. Operators should still avoid putting secrets in command payloads; secret distribution belongs in an appropriate secret-management system.

The sample worker supports two illustrative dynamic values:

```json
{
  "targetUrl": "https://example.com",
  "runIntervalSeconds": 60
}
```

Real components define their own payload contract.

## Audit trail

Command transitions use the existing durable audit subsystem:

Operator actor:

- `component-command.issued`
- `component-command.cancelled`

Component actor:

- `component-command.succeeded`
- `component-command.failed`
- `component-command.rejected`

System actor:

- `component-command.expired`

Operator issuance/cancellation is staged in the same EF Core save boundary as the command mutation. Component acknowledgements stage the terminal command transition, component control-state transition, and component-actor audit row in one save boundary.

Audit snapshots contain safe command metadata and result/error state but not `PayloadJson` or lease tokens.

## Operator UI

Component detail (`/components/{id}`) contains:

- current control state;
- command issuance form;
- active-run selector for KillRun;
- optional RefreshConfiguration JSON;
- expiry setting;
- recent command history and cancellation.

`/commands` is the cross-component operational history with filters for time, component, command type, status, and text. Issuance intentionally remains component-scoped so potentially disruptive operations are performed with explicit target context.

## Configuration

```json
{
  "ComponentCommands": {
    "Enabled": true,
    "LeaseSeconds": 30,
    "SweepIntervalSeconds": 15,
    "DefaultExpiryMinutes": 15,
    "MaxDeliveryAttempts": 10
  }
}
```

Equivalent environment variables use normal ASP.NET Core syntax, for example `ComponentCommands__LeaseSeconds=30`.

The expiry worker marks abandoned commands terminal after their absolute expiry or after the configured delivery-attempt ceiling. An actively leased command is allowed to use its lease window before expiry processing can terminalize it.

## .NET client

Control polling is intentionally separated from telemetry lifecycle calls:

```csharp
var control = new MonitorControlClient(httpClient, componentKey);

while (!cancellationToken.IsCancellationRequested)
{
    var command = await control.ClaimNextAsync(componentId, cancellationToken);
    if (command is null)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        continue;
    }

    switch (command.Type)
    {
        case ComponentCommandType.Pause:
            // stop accepting new local work
            await control.SucceedAsync(command, new { paused = true }, cancellationToken);
            break;

        default:
            await control.RejectAsync(command, "Unsupported by this component.", cancellationToken: cancellationToken);
            break;
    }
}
```

`MonitorControlClient` does not execute commands for the component. It implements the transport and acknowledgement contract; command semantics remain owned by the workload/runtime.

## Integration proof

The SQL Server integration gate deliberately verifies:

- component B cannot claim component A's queue (`403`);
- an unacknowledged lease is redelivered using the same command id and a new token;
- the old token receives `409` after re-lease;
- duplicate completion of an already-succeeded command is idempotent;
- Pause and Disable block new runs with `409` while heartbeat/existing-run completion remains valid;
- Resume and Enable restore new-run admission;
- operator cancellation removes a command from the claimable queue;
- abandoned commands become Expired and receive a system audit event;
- command payload markers do not leak into audit JSON;
- `ComponentCommands` has no FK to `Runs`.
