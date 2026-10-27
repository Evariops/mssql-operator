# `SqlAvailabilityGroupDatabase`

> Joins a single user database into an existing SQL Server Always On availability group and keeps its synchronization state reconciled.

`apiVersion: mssql-operator.evariops.com/v1alpha1` · `kind: SqlAvailabilityGroupDatabase` · scope: Namespaced · plural: `sqlavailabilitygroupdatabases`

## Minimal example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
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
| `dataMovement.requireManualResumeAfterDataLoss` | boolean | no | `true` | After a forced failover that accepted data loss, the replicas holding divergent log — the previous primary, and any secondary that synchronized from it beyond the fork point (`lastFailover.forkLsns` of the AG) — are not resumed until the `mssql-operator.evariops.com/approve-data-loss-resume=true` annotation is set on this resource: resuming them rolls back commits that were never replicated. The annotation is consumed and covers that failover only (`status.dataLossResumeApprovedFor`). Every other stalled secondary is resumed automatically. Set to `false` to resume divergent replicas without approval. |

## Status

| Field | Type | Description |
| --- | --- | --- |
| `phase` | string | Lifecycle phase. One of: `Pending`, `WaitingForAg`, `Adding`, `Seeding`, `Synchronized`, `Synchronizing`, `Suspended`, `NotSynchronizing`, `Removing`, `Failed`. |
| `observedGeneration` | integer (int64) | The `metadata.generation` last processed by the controller. |
| `availabilityGroupName` | string | Resolved AG name the database was added to. |
| `databaseName` | string | Resolved database name. |
| `replicaStates` | array | Per-replica synchronization state (see below). Kept inline under the etcd-light status model because it is bounded by replica count (≤ 9). |
| `consecutiveResumeAttempts` | integer | Consecutive HADR RESUME attempts that did not bring the database back. |
| `fruitlessResumeRounds` | integer | Rounds of RESUME attempts that ended without the database coming back; at the second, the stuck replica is re-seeded. |
| `reseededAt` | map (replica → date-time) | When each replica was last re-seeded. |
| `reseedAttempts` | map (replica → integer) | Seeding attempts of the current re-seed of each replica. |
| `dataLossResumeApprovedFor` | string (date-time) | Timestamp of the data-loss failover whose divergent replicas were approved to rejoin (the consumed `approve-data-loss-resume` annotation). |
| `conditions` | array | Standard conditions: `AgReady`, `DatabaseExists`, `Ready`, and when relevant `ManualRejoinRequired`, `ResumeIneffective`, `SuspendedBySpec`, `SyncStateUnknown`, `LogBackupsRequired`, `TransactionLogFull` (the log on the primary is at least 80% full and cannot be truncated; the reason — `LogBackup`, `AvailabilityReplica`, `ActiveTransaction` — says what holds it), `ReseedBlocked`, and `NeedsAttention` — True only while a human decision is pending (`ManualRejoinRequired`, `TransactionLogFull` or `ReseedBlocked`), exported as `mssqloperator_needs_attention`. |

`kubectl get sqlavailabilitygroupdatabase` shows: `PHASE` (`.status.phase`), `AG` (`.status.availabilityGroupName`), `DATABASE` (`.status.databaseName`), and `AGE`.

### `replicaStates[]`

| Field | Type | Description |
| --- | --- | --- |
| `replica` | string | Replica (instance) name. |
| `role` | string | Replica role: `Primary`, `Secondary`, or `Unknown`. |
| `synchronizationState` | string | `Synchronized`, `Synchronizing`, `NotSynchronizing`, `Suspended`, or `Unknown`. |
| `isFailoverReady` | boolean | Whether this replica is in a state safe for failover. |

> Queue sizes (log-send / redo) are live telemetry and deliberately **not** in the status: they change every reconcile and would cause an etcd write/watch hot-loop. They are served by the `view.mssql-operator.evariops.com` API and the Prometheus metrics instead.

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
- **Suspend/resume:** If `dataMovement.suspended` is `true`, the operator suspends data movement on the primary. When it goes back to `false`, the operator resumes on the primary what it suspended itself.
- **NotSynchronizing self-heal (HADR RESUME):** When a secondary is suspended or `NOT SYNCHRONIZING`, the operator issues `HADR RESUME` on that secondary; when a plain RESUME did not converge, it runs `SET (ROLE = SECONDARY)` then SUSPEND/RESUME there (a secondary left pointing at a demoted primary). A replica that does not report the secondary role yet (RESOLVING, or a resurrected former primary) is left to the failover orchestrator and not counted. After five fruitless attempts the condition `ResumeIneffective=True` is set and a new round starts five minutes later. Divergent replicas after a data-loss failover wait for the approval described under `requireManualResumeAfterDataLoss`.
- **Automatic re-seed:** a secondary that stays stuck through two rounds of RESUME attempts (about ten minutes) is re-seeded: its copy is taken out of the AG and dropped, and the primary is asked to seed it again (`ReplicaReseeded` event, `status.reseededAt`). A seeding that fails is started again, up to five attempts (`status.reseedAttempts`): with the log backed up every minute, a log backup between the seeding and the join fails it (error 1412 on the replica) — the primary no longer holds the log for a copy taken out of the AG. Its copy holds nothing the primary lacks — a replica that diverged after a data-loss failover is never re-seeded without the data-loss approval, which then covers the re-seed too. Never during a failover or a rolling update, not twice within the hour for the same replica, only with `seedingMode: Automatic` and when the replica's volumes can hold the copy; anything that blocks it sets `ReseedBlocked=True` and `NeedsAttention=True`. The `mssql-operator.evariops.com/reseed-replica=<replica>` annotation forces a re-seed now (consumed; an explicit decision, so it applies to a divergent copy as well).
- **Failed is not terminal:** `Failed` resources are re-evaluated from scratch (requeued every 60s) so transient errors self-recover. Other phases requeue every 30s.

## Related

- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md)
- [`SqlTdeCertificate`](./sqltdecertificate.md)
