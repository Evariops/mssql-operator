# Availability groups

How to run an Always On availability group with the operator: connect to it, add databases, choose
its durability, fail over, upgrade it, and what it repairs by itself. Field reference:
[`SqlAvailabilityGroup`](../reference/sqlavailabilitygroup.md).

The AG runs without Windows or Pacemaker clustering (`CLUSTER_TYPE = NONE`). The operator is the
cluster manager: it watches the replicas every 10 seconds and decides failovers.

## Connect

| Service | Use |
|---|---|
| `<ag>-listener:1433` | read/write, always the primary |
| `<ag>-ro:1433` | read-only: readable secondaries that are connected and synchronized; the primary when there is none (`readOnlyListener.fallbackToPrimary`, default `true`) |

With `readOnlyRouting.enabled: true`, clients connecting to the listener with
`ApplicationIntent=ReadOnly` are also routed to a secondary by SQL Server.

## Add a database

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata: { name: orders-db, namespace: sql }
spec:
  availabilityGroupRef: { name: orders }
  databaseName: Orders
```

The operator creates the database on the primary (or adopts it if it exists), takes the
initialization backup, adds it to the AG through a `SqlAvailabilityGroupDatabase` it manages, and
seeds the secondaries. Watch `kubectl get sqldb -n sql` until `Ready`. Options (collation, files,
owner, log backups): [`SqlDatabase`](../reference/sqldatabase.md).

A database created by other means joins the AG with a `SqlAvailabilityGroupDatabase` alone.

Log backups: unless you set `logBackup.mode: Disabled`, the operator backs up the log when it is half
full and nothing else did. See [Backups and encryption](backups-and-encryption.md).

## Choose the durability

`spec.replicas.durability` decides what happens when synchronous secondaries drop.

| | `Quorum` | `Legacy` |
|---|---|---|
| A commit is acknowledged when | it is hardened on the primary **and** a synchronized secondary | it is hardened on the primary (and on a secondary while one is synchronized) |
| No synchronized secondary left | writes **block** until one is back (event `DurabilityFloorHoldingWrites`) | writes continue, durability lowered (event `RequiredSyncLowered`, condition `DurabilityAtRisk`) |
| Automatic failover | never loses an acknowledged commit | may lose recent writes, so it happens only with `allowAutomaticDataLossFailover: true` |
| Needs | 2 synchronous secondaries (`synchronousCount: 3` or more) | anything |

Default: `Quorum` for a new Enterprise AG of at least 3 replicas, `Legacy` otherwise and for AGs
created before this setting existed.

To move an existing AG to `Quorum`, set `durability: Quorum`. The operator makes the needed replicas
synchronous one at a time (events `AvailabilityModeRaised`, then `DurabilityQuorumReached`). Commit
latency may grow. Until all are synchronized the AG stays `Legacy`, with `DurabilityAtRisk`
(reason `ConvergingToQuorum`).

A `Legacy` AG can still refuse to lower durability below one secondary: `replicas.requiredSyncFloor: 1`.

## Failovers

### Automatic

When no replica answers as primary for 3 health passes (about 30 s), the operator:

1. makes sure the old primary cannot accept writes any more: it demotes it if it can reach it,
   otherwise it fences it (deletes its pod and waits until the kubelet confirms it is gone, or until
   its volumes are detached). If it can prove neither, it does not promote (`FailoverAborted`) rather
   than risk two primaries. Storage without VolumeAttachments (NFS, local-path) cannot prove a lost
   node's isolation: use a CSI driver that attaches volumes;
2. promotes the synchronized secondary that holds the most recent log;
3. moves the listener and the read-only service.

When the old primary comes back, it is demoted to secondary and resynchronizes
(`ResurrectedPrimaryDemoted`). For a primary that is down or cut off, expect one to one and a half
minutes without writes.

If no synchronized secondary is left, an automatic failover would lose data: the operator waits
(`NeedsAttention`, reason `NoLosslessFailoverTarget`) unless `failover.allowAutomaticDataLossFailover`
is `true`.

### Planned

```bash
kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/failover-to=orders-1
```

Lossless: refused when the target is not synchronized. The annotation is removed once done; follow it
with `kubectl get sqlavailabilitygroup orders -n sql -w`.

### Forced, with data loss

Only when you accept losing what the target has not received:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql \
  mssql-operator.evariops.com/force-failover-to=orders-2 \
  mssql-operator.evariops.com/allow-data-loss=true
```

After a failover with data loss, replicas that hold the lost commits do not rejoin by themselves. The
`SqlAvailabilityGroupDatabase` reports it; let them rejoin (their extra commits are discarded) with
`mssql-operator.evariops.com/approve-data-loss-resume=true` on that resource.

## Rolling updates

Changing the SQL Server build (`instanceTemplate.cumulativeUpdate`), the image, resources or any pod
setting rolls the AG: secondaries first, one at a time, each waited for until healthy and
synchronized, then a planned failover, then the old primary. `pauseBetweenPodsSeconds` (60 s) spaces
them. Progress: `status.rollingUpdate`.

- Across the cluster, at most `operator.maxConcurrentRestarts` (1) resources restart at once; the
  others wait (`RestartDeferred`).
- A rollout that cannot proceed stops in `Failed` (`NeedsAttention`, reason `RollingUpdateFailed`).
  Fix the cause, then `kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/retry-rolling-update=true`.

Version rules: [Upgrading](upgrading.md#sql-server-builds).

## What the operator repairs by itself

| Problem | What happens |
|---|---|
| A secondary stops synchronizing | resumed; re-seeded from the primary if still stuck after about 10 minutes |
| A replica stays disconnected while the log fills (85 %) | removed from the AG after `removeDisconnectedReplicaAfterMinutes` (30), added back and re-seeded when it returns |
| A volume fills up | data and log volumes grown online at 80 %, up to 4 × their declared size (needs a StorageClass with `allowVolumeExpansion`) |
| A replica loses its volume | the new volume is created, grown to the size of its peers, and the database re-seeded |
| A node is lost | the AG continues without that replica. Its pod is moved elsewhere automatically after 10 minutes only if its storage driver is declared trusted; otherwise `NeedsAttention` (`ReplicaOnLostNode`) and the [lost node runbook](../runbooks/lost-node.md) |
| Two replicas on one node | condition `ReplicasColocated`: losing that node takes both |

When it cannot decide alone, the AG or database gets `NeedsAttention=True` with a message saying what
to do. That condition is the one to alert on ([Observability](observability.md)).

## Placement

By default the replicas prefer distinct nodes and zones. `topologySpread.self.node: Required` (or
`zone`) makes it strict; with more replicas than zones, the extra replica stays `Pending`. The
operator pods also prefer nodes without SQL Server, so losing one node rarely takes both a primary and
the operator leader.

## Standard edition

Exactly 2 replicas, one database per AG, no readable secondary, no read-only listener, no backup on
the secondary. Durability is `Legacy`.
