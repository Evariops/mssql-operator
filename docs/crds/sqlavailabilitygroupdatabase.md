# `SqlAvailabilityGroupDatabase`

> Joins a single user database into an existing SQL Server Always On availability group and keeps its synchronization state reconciled.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlAvailabilityGroupDatabase` · scope: Namespaced · plural: `sqlavailabilitygroupdatabases`

## Minimal example

```yaml
apiVersion: mssql.operator/v1alpha1
kind: SqlAvailabilityGroupDatabase
metadata:
  name: orders-db
  namespace: sql
spec:
  availabilityGroupRef:
    name: prod-ag
  databaseName: Orders
```

## Overview

A `SqlAvailabilityGroupDatabase` (short name `sagdb`) declares that one database should be a member of the availability group referenced by `spec.availabilityGroupRef`. The operator connects to the AG primary replica, validates the database exists, adds it to the AG, drives initial seeding (automatic or manual), and then continuously monitors its per-replica synchronization state. Each resource manages exactly one database. To add several databases to the same AG, create several resources.

## Spec

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `availabilityGroupRef` | object | yes |  | Reference to the parent `SqlAvailabilityGroup` in the same namespace. Immutable after creation. |
| `availabilityGroupRef.name` | string | yes |  | Name of the `SqlAvailabilityGroup`. |
| `databaseName` | string | yes |  | Name of the database to join. Must already exist on the primary, be ONLINE and in FULL recovery model. Immutable after creation. Must match `^[a-zA-Z_][a-zA-Z0-9_]{0,127}$` and must not be a system database. |
| `seedingMode` | string (enum) | no | `Automatic` | How the database is seeded to secondaries. Allowed values: `Automatic`, `Manual` (case-insensitive). |
| `dataMovement` | object | no | `null` | Optional data-movement (suspend/resume) control. |
| `dataMovement.suspended` | boolean | no | `false` | When `true`, the operator issues `ALTER DATABASE ... SET HADR SUSPEND` for the database. When `false`, the operator may resume movement (subject to the gate below). |
| `dataMovement.requireManualResumeAfterDataLoss` | boolean | no | `true` | When `true`, the operator never automatically resumes data movement. This blocks automatic `HADR RESUME` after a forced failover where data divergence is possible. Set to `false` to allow the operator to auto-resume. |

## Status

| Field | Type | Description |
| --- | --- | --- |
| `phase` | string | Lifecycle phase. One of: `Pending`, `WaitingForAg`, `Adding`, `Seeding`, `Synchronized`, `Synchronizing`, `Suspended`, `NotSynchronizing`, `Removing`, `Failed`. |
| `observedGeneration` | integer (int64) | The `metadata.generation` last processed by the controller. |
| `availabilityGroupName` | string | Resolved AG name the database was added to. |
| `databaseName` | string | Resolved database name. |
| `replicaStates` | array | Per-replica synchronization state (see below). |
| `conditions` | array | Standard conditions: `AgReady`, `DatabaseExists`, `Ready`. |

### `replicaStates[]`

| Field | Type | Description |
| --- | --- | --- |
| `replica` | string | Replica (instance) name. |
| `role` | string | Replica role: `Primary`, `Secondary`, or `Unknown`. |
| `synchronizationState` | string | `Synchronized`, `Synchronizing`, `NotSynchronizing`, `Suspended`, or `Unknown`. |
| `isFailoverReady` | boolean | Whether this replica is in a state safe for failover. |
| `logSendQueueSize` | integer (int64) | Size of the log send queue (KB). |
| `redoQueueSize` | integer (int64) | Size of the redo queue (KB). |

### `conditions[]`

| Field | Type | Description |
| --- | --- | --- |
| `type` | string | Condition type: `AgReady`, `DatabaseExists`, or `Ready`. |
| `status` | string | `True` or `False`. |
| `reason` | string | Machine-readable reason. |
| `message` | string | Human-readable detail. |
| `lastTransitionTime` | string (date-time) | When the condition last changed. |

`Ready` is `True` only when `phase` is `Synchronized`.

## Validation & behaviour

### Webhook validation

On create and update the admission webhook enforces:

- `availabilityGroupRef.name` is required.
- `databaseName` is required, must match `^[a-zA-Z_][a-zA-Z0-9_]{0,127}$`, and may not be one of `master`, `msdb`, `model`, `tempdb`.
- `seedingMode` must be `Automatic` or `Manual` (case-insensitive).

On update, `availabilityGroupRef.name` and `databaseName` are immutable. Changing either is rejected with HTTP 422 (delete and recreate instead).

Cross-CRD checks run on create (skipped during dry-run, and fail-open if they error):

- The referenced `SqlAvailabilityGroup` must exist in the same namespace.
- For Standard Edition (Basic AG), at most one database is allowed per AG.
- The same database may not be referenced twice in the same AG (uniqueness, case-insensitive).

### Reconcile behaviour

- **Waiting / Pending:** The operator resolves the AG, waits until the AG `status.phase` is `Running` and a primary replica is known, then connects to the primary (`<primary>.<ag>-headless.<ns>.svc.cluster.local:1433`) using the SA password from the AG's secret. If the database does not exist on the primary, or already belongs to a different AG, the resource goes to `Failed`. If it is already in this AG, it transitions straight to `Synchronized` monitoring.
- **Adding / seeding:** The database is prepared (backup taken) and added to the AG. With `seedingMode: Automatic` the phase becomes `Seeding`. With `Manual` it becomes `Synchronizing`. Seeding and synchronizing phases are requeued every 15s.
- **Monitoring:** Once added, the operator reads per-replica states and sets `phase` to `Synchronized`, `Synchronizing`, `Suspended`, or `NotSynchronizing` accordingly. If the database is no longer present in the AG, the resource goes to `Failed`.
- **Suspend/resume:** If `dataMovement.suspended` is `true` and no replica is suspended yet, the operator suspends data movement. If `suspended` is `false` while a replica is suspended, it resumes only when `requireManualResumeAfterDataLoss` is not `true`.
- **NotSynchronizing self-heal (HADR RESUME):** When a replica reports `NOT SYNCHRONIZING`, the operator issues `HADR RESUME` to restart synchronization, but only when `requireManualResumeAfterDataLoss` is not `true` and `suspended` is not `true`. With the default `requireManualResumeAfterDataLoss: true`, recovery is gated and requires manual intervention, avoiding silent resume after a forced failover with possible data loss.
- **Failed is not terminal:** `Failed` resources are re-evaluated from scratch (requeued every 60s) so transient errors self-recover. Other phases requeue every 30s.

## Related

- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md)
- [`SqlTdeCertificate`](./sqltdecertificate.md)
