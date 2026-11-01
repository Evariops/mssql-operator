# Availability groups

This guide covers the use of a `SqlAvailabilityGroup`: prerequisites, declaration, connections, durability, failovers, rolling updates and placement. The internal mechanisms are described in [Availability group internals](../architecture/availability.md), and every field is listed in the [reference](../reference/resources.md#sqlavailabilitygroup).

## Prerequisites

The StorageClass must use a CSI driver that attaches volumes to nodes (`attachRequired: true`). When a primary is lost together with its node, the only proof that it can no longer write is the removal of its VolumeAttachments. With NFS, local-path or hostPath storage, that proof is impossible, and the failover waits for a human. With `allowVolumeExpansion: true`, the operator grows data and log volumes before they fill up.

Each replica should run on its own node. By default, replicas prefer distinct nodes and zones, and the AG reports the condition `ReplicasColocated` when two of them share a node.

Set `instanceTemplate.memory.total` (at least 4Gi). The operator derives `memorylimitmb` and `max server memory` from it. Without it, SQL Server sizes itself at 80 % of the container limit, and memory allocated outside the buffer pool can get the container killed.

## Declaration

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: orders
  namespace: sql
  annotations:
    mssql-operator.evariops.com/safety-level: "1"
spec:
  edition: Enterprise
  replicas:
    count: 3                     # immutable
    synchronousCount: 3          # replicas in synchronous commit, the primary included
    durability: Quorum
  saPasswordSecretRef: { name: orders-sa, key: password }
  masterKeyPasswordSecretRef: { name: orders-dmk, key: password }
  hadrCertificate: { source: Generate, model: PerReplica }
  instanceTemplate:
    version: "2022"
    cumulativeUpdate: CU25
    memory: { total: 16Gi }
    resources:
      requests: { cpu: "2" }
      limits: { cpu: "4" }
    storage:
      data: { size: 50Gi, storageClassName: fast-ssd }
      log:  { size: 20Gi, storageClassName: fast-ssd }
  listener:
    service: { type: ClusterIP }
  readOnlyListener:
    service: { type: ClusterIP }
```

Both Secrets must exist beforehand, with a `password` key. The operator uses the `sa` password to manage the replicas. The master key password protects the database master key, which protects the endpoint certificates and the TDE certificates. They are separate so that an `sa` rotation never affects encryption.

The operator starts the replicas, creates an endpoint certificate on each, creates the AG on the first replica and joins the others. `kubectl get sqlavailabilitygroup orders -w` shows the phase and the primary, and `kubectl get agreplicastates` the live state of each replica.

## Connections

The Service `orders-listener` (port 1433) selects the primary. The Service `orders-ro` selects the readable secondaries that are connected and synchronized. When none qualifies, for example during a partition between replicas, `orders-ro` selects the primary instead (`readOnlyListener.fallbackToPrimary`, `true` by default). A disconnected secondary never serves reads through it.

With `readOnlyRouting.enabled: true`, SQL Server read-only routing is configured as well: a client that connects to the listener with `ApplicationIntent=ReadOnly` is redirected to a secondary.

A failover breaks existing connections. Applications must reconnect and retry the transaction in progress. A commit whose acknowledgement was lost during a failover may have been applied, so retried writes should be idempotent.

## Databases

A database joins the AG through a `SqlDatabase`:

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata: { name: orders-db, namespace: sql }
spec:
  availabilityGroupRef: { name: orders }
  databaseName: Orders
  availabilityGroup:
    logBackup: { mode: Local, retentionHours: 72 }
```

The operator creates the database on the primary, or adopts an existing one. It takes the initialization backup, adds the database to the AG and seeds the secondaries. Options, ownership and deletion are described in [Databases](databases-and-access.md#databases), and log backups in [Backups and encryption](protecting-data.md#log-backups-and-the-log-chain).

## Durability

`spec.replicas.durability` defines what happens when no synchronous secondary is synchronized any more.

With `Quorum`, `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` never goes below 1. A commit is acknowledged only once it is hardened on the primary and on a synchronized secondary. When no secondary is synchronized, writes block until one returns, and the operator publishes `DurabilityFloorHoldingWrites`. Every automatic failover is then lossless. `Quorum` requires at least two synchronous secondaries (`synchronousCount: 3` or more), so that losing one does not block writes.

With `Legacy`, the operator lowers `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` to 0 when the synchronous secondaries drop (`RequiredSyncLowered`, condition `DurabilityAtRisk`) and restores it when one is synchronized again. Writes continue, but the primary acknowledges commits that exist nowhere else, and a failover during that window can lose them. For that reason, the operator does not fail over automatically in that situation unless `failover.allowAutomaticDataLossFailover` is `true`. `replicas.requiredSyncFloor: 1` keeps a `Legacy` AG from going below one secondary.

When the field is not set, a new Enterprise AG with `synchronousCount: 3` or more gets `Quorum`, and other AGs, including those created before the field existed, get `Legacy`. Since `synchronousCount` defaults to 2, an AG that sets neither field runs `Legacy`, and the webhook says so at creation. An AG created before this rule keeps the profile recorded at its creation. Setting the field explicitly is recommended. Setting `Quorum` on an existing AG raises the replicas to synchronous commit one at a time (`AvailabilityModeRaised`, then `DurabilityQuorumReached`). Commit latency can increase, since every commit then waits for a secondary.

## Failovers

### Automatic failover

The operator starts a failover when no replica answers as primary for three health passes and at least 20 seconds, and the reachable secondaries report that they are disconnected from the primary. It then performs three steps:

1. It isolates the old primary: a demotion if it answers, otherwise a pod deletion confirmed by the kubelet, or the removal of its VolumeAttachments if its node is NotReady. When none of these can be confirmed, the failover is aborted (`FailoverAborted`).
2. It promotes the secondary with the highest hardened LSN, provided it can establish that this secondary holds every acknowledged commit.
3. It moves the writer label, so that the listener follows the new primary.

After a hard failure of the primary (frozen process, network isolation), writes stop for about 80 to 120 seconds. A primary pod that simply restarts usually comes back as primary within 15 to 20 seconds, before any failover is started. When the old primary returns after a failover, the operator demotes it to secondary (`ResurrectedPrimaryDemoted`).

When the operator cannot establish that a promotion is lossless, it does not promote and raises `NeedsAttention` with reason `NoLosslessFailoverTarget`. The message states the missing condition. A primary that is unreachable from the operator and its secondaries may still be serving its clients, so the message should be checked before forcing a failover. Each case is described in [Failure modes](../failure-modes.md#the-primary).

### Planned failover

```bash
kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/failover-to=orders-1
kubectl get sqlavailabilitygroup orders -n sql -w
```

A planned failover is lossless and takes a few seconds. It is rejected (`FailoverRejected`) when the target is not synchronized on every database. The annotation is removed when the failover completes, or after three failed attempts (`FailoverAbandoned`).

### Forced failover

When the primary is lost and no lossless target exists, only a forced failover can restore writes:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql \
  mssql-operator.evariops.com/force-failover-to=orders-2 \
  mssql-operator.evariops.com/allow-data-loss=true
```

The replicas that may hold commits the new primary lacks do not rejoin automatically. These are the old primary and any replica that synchronized from it after the failover point. Once the data needed from them is recovered, `mssql-operator.evariops.com/approve-data-loss-resume=true` on the `SqlAvailabilityGroupDatabase` lets them rejoin, and their extra commits are discarded. The procedure is detailed in [Failure modes](../failure-modes.md#forced-failover-with-data-loss).

## Rolling updates

A change of the SQL Server build (`instanceTemplate.cumulativeUpdate`), of the image, of the resources or of any pod setting restarts SQL Server. On an AG, the operator applies it as a rolling update:

1. It restarts the secondaries one at a time. Each secondary's data movement is suspended before its pod is deleted, so that commits do not stall, and the next pod waits until it is healthy and synchronized again.
2. It waits `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` (60 seconds) between pods.
3. It runs a planned failover to an updated secondary.
4. It restarts the old primary, now a secondary.

The cost is one planned failover. Progress is reported in `status.rollingUpdate`. At most `operator.maxConcurrentRestarts` resources (1 by default) restart at the same time across the cluster, and the others report `RestartDeferred`. A rollout that cannot continue stops in phase `Failed` with `NeedsAttention`. Once the cause is fixed, the following annotation resumes it:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/retry-rolling-update=true
```

The rules on SQL Server builds (no downgrade, published builds only, approval of new defaults) are in [Operations](operating.md#sql-server-builds).

### Pausing an AG

The annotation `mssql-operator.evariops.com/reconcile-paused=true` stops all reconciliation of the AG. The breakout controller also leaves its pods alone, and the AG reports `ReconcilePaused`. No health check or failover runs while the annotation is present, so a primary failure during that time is not handled. Removing the annotation (`reconcile-paused-`) resumes reconciliation.

## Automatic repairs

The operator handles the following situations without intervention:

- A secondary that stops synchronizing is resumed, and re-seeded from the primary if it stays stalled for about ten minutes.
- A replica that stays disconnected for 30 minutes while the primary's log is at least 85 % full is removed from the AG, then added back and re-seeded when it returns.
- Data and log volumes are grown online at 80 % usage, up to four times their declared size.
- A replica that comes back with empty volumes is re-provisioned and re-seeded.
- An old primary that comes back after a failover is demoted.
- A deleted Service, StatefulSet or key Secret is recreated.

When a decision requires a human, the AG or the database raises `NeedsAttention=True` with a message describing the action to take. This is the condition to alert on ([Operations](operating.md#monitoring)).

## Placement

Replicas prefer distinct nodes and zones by default. `topologySpread.self.node: Required` (or `zone`) makes the constraint strict, in which case a replica that cannot be placed stays `Pending`. The operator pods prefer nodes without SQL Server, which reduces the chance of losing the primary and the operator leader together.

## Standard edition

A Standard edition AG is a basic availability group: two replicas exactly, one database, no readable secondary, no read-only listener, and no backups on the secondary. Its durability is always `Legacy`.
