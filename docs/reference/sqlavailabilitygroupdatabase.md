# SqlAvailabilityGroupDatabase

Puts one database into an availability group and keeps it synchronized on every replica. One resource
per database. Short name: `sagdb`.

A [`SqlDatabase`](sqldatabase.md) on an AG creates and manages this resource for you (annotation
`mssql-operator.evariops.com/sqldatabase`): edit the `SqlDatabase` instead. Write one yourself only
for a database created by other means.

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroupDatabase
metadata:
  name: orders-db
  namespace: sql
spec:
  availabilityGroupRef: { name: orders }   # the SqlAvailabilityGroup, same namespace
  databaseName: Orders
  logBackup:
    mode: Local                            # or Disabled when your own tooling backs up the log
    retentionHours: 72
```

The database must already exist on the primary. The operator then takes the initialization backup if
the database has none, adds it to the AG, and seeds the secondaries.

## Main fields

| Field | Default | What it does |
|---|---|---|
| `availabilityGroupRef.name` | required | The AG. Immutable. |
| `databaseName` | required | The database. Immutable, not a system database. Converted to FULL recovery if needed (event `RecoveryModelConverted`). |
| `seedingMode` | `Automatic` | `Automatic`: the primary copies the database to the secondaries. `Manual`: you restore it there yourself. |
| `logBackup.mode` | unset (deprecated) | `Local`: the operator backs up the log when it is 50 % full and nothing else truncated it, into `/var/opt/mssql/backup/mssql-operator/` on the primary. `Disabled`: your tooling owns log backups. Unset works like `Local` with a 24 h retention and raises `LogBackupModeDeprecated`: set it. |
| `logBackup.thresholdPercent` | `50` | Log usage that triggers an operator log backup. |
| `logBackup.retentionHours` | none in `Local` | Delete the operator's own log backups after this many hours. Without it, in `Local` mode, they are kept: they are part of the restore chain. |
| `initialization.backupWindow` | none | Daily window (`start` in UTC, `durationMinutes`) for the initialization backup of a large database. |
| `dataMovement.suspended` | `false` | Suspend data movement (`ALTER DATABASE … SET HADR SUSPEND`). |
| `dataMovement.requireManualResumeAfterDataLoss` | `true` | After a failover that lost data, replicas holding the lost commits rejoin only with the `approve-data-loss-resume` annotation. |

## Self-healing

- A secondary that stops synchronizing is resumed (`HADR RESUME`). If it stays stuck for about ten
  minutes, its copy is dropped and re-seeded from the primary (`ReplicaReseeded`). The re-seed is
  refused when the replica's volume cannot hold the copy (`ReseedBlocked`, with the size needed).
- Replicas that hold commits lost in a forced failover are never resumed automatically.

## Annotations

| Annotation | Effect |
|---|---|
| `mssql-operator.evariops.com/reseed-replica: <replica>` | Re-seed that replica now. |
| `mssql-operator.evariops.com/approve-data-loss-resume: "true"` | Let the divergent replicas of the last data-loss failover rejoin. |

## Status

```console
$ kubectl get sagdb -n sql
NAME        PHASE          AG       DATABASE   AGE
orders-db   Synchronized   orders   Orders     3d
```

- `status.phase`: `Pending`, `WaitingForAg`, `Adding`, `Seeding`, `Synchronizing`, `Synchronized`,
  `Suspended`, `NotSynchronizing`, `Removing`, `Failed` (retried every minute).
- `status.replicaStates[]`: synchronization state of the database on each replica.
- `status.conditions`: **`NeedsAttention`** when a decision is needed: transaction log full and not
  truncatable (`TransactionLogFull`, the reason says what holds it), re-seed blocked, or a manual
  rejoin required. `LogChainHeldByOperator` says the operator's log backups are part of the restore chain.

Backups recorded for the database on each replica: `kubectl get logchains -n sql`.

Deleting the resource removes the database from the AG; the database itself stays on the primary.
