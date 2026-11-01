# Failure modes

This page describes the failures the operator handles: what happens, what the operator does, the events and conditions to expect, and the manual steps when there are any. The mechanisms behind these behaviours are described in [Availability group internals](architecture/availability.md) and [Safety mechanisms](architecture/safety.md).

The operator applies the same rules in every case. It never lets two replicas accept writes, and it never loses an acknowledged commit without a human decision; when this conflicts with availability, writes stop. When it cannot establish that an action is safe, it does not take it, and raises `NeedsAttention` with a message that states what could not be established.

## Triage

The condition to alert on is `NeedsAttention=True`, exported as `mssqloperator_needs_attention`. Its reason points to the relevant section:

| Reason | Section |
|---|---|
| `NoLosslessFailoverTarget`, `DataLossFailoverBlocked`, `FailoverRefusedUnknownPrimary` | [Primary cut off from its secondaries but reachable by clients](#primary-cut-off-from-its-secondaries-but-reachable-by-clients) |
| `SplitBrain` | [Return of the old primary](#return-of-the-old-primary) |
| `ReplicaOnLostNode`, `InstanceOnLostNode` | [A node is lost](#a-node-is-lost) |
| `TransactionLogFull` | [Transaction log full](#transaction-log-full) |
| `VolumeFull` | [Volume full](#volume-full) |
| `ReseedBlocked` | [Secondary not synchronizing](#secondary-not-synchronizing) |
| `ManualRejoinRequired` | [Forced failover with data loss](#forced-failover-with-data-loss) |
| `RollingUpdateFailed` | [Rolling updates](guide/availability-groups.md#rolling-updates) |
| machine key changed or missing, `sa` password refused | [Key Secret deleted or replaced](#key-secret-deleted-or-replaced) |

These commands give most of the context:

```bash
kubectl get events -n <ns> --field-selector involvedObject.name=<ag> --sort-by=.lastTimestamp
kubectl get agreplicastates -n <ns>     # role, connection and synchronization of each replica
kubectl get operatorplans -n <ns>       # actions executed, withheld or awaiting approval
```

## Test conditions

The durations below were measured during chaos campaigns in October 2026 on a Talos cluster with Cilium. The AG had three replicas (Developer edition, three synchronous replicas, `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` = 1), a listener and a read-only listener. A probe committed one row every 100 ms through the listener, on a new connection each time, and logged each acknowledged commit. The write gap of a scenario is the longest interval between two acknowledged commits; data loss means an acknowledged row missing afterwards. Four other writers generated load, and a reader queried the read-only listener every second. Network faults were injected with CiliumNetworkPolicy only.

The first campaign covered about forty scenarios, the second replayed them on the next release and added the safety level features, and a third covered databases. Each scenario that failed led to a fix and was replayed.

These campaigns have known gaps. The three replicas ran on the same node, so a node failure was a total outage and never a failover. The storage was local LVM without VolumeAttachments, so fencing by volume detachment was not exercised on real hardware. A CiliumNetworkPolicy does not cut established connections, which made detection in network scenarios slower than with a real partition.

## The primary

### Primary pod deleted or restarted

The pod stops in a few seconds: the `preStop` hook runs a `CHECKPOINT` and then sends `SIGTERM` to `sqlservr`. The StatefulSet recreates the pod, and SQL Server comes back as primary, usually before the 20-second detection window ends, so no failover takes place. A forced deletion (`--grace-period=0 --force`) behaves the same way. In the campaigns, writes stopped for 12.7 to 16.6 seconds, with no data loss.

The SQL Server image starts `sqlservr` from a shell script that does not forward signals. Before the operator's `preStop` hook sent `SIGTERM` itself, every pod deletion waited for the full 180-second grace period and ended with `SIGKILL`.

### SQL Server crash

After `kill -9`, an access violation or an OOM kill, the container restarts, runs recovery and comes back as primary. If recovery takes longer than the detection window, the operator fails over (next section) and demotes the restarted replica when it returns. After `kill -9`, writes stopped for 17.5 to 18 seconds, with no data loss.

### Frozen primary

A frozen process (`SIGSTOP`, a blocked kernel call, a node swapping heavily) keeps its TCP connections open but answers nothing. Each health probe is bounded: a connection must answer a `SELECT 1` within 5 seconds, and a probe that exceeds its deadline is abandoned and the replica counted unreachable. Before this bound existed, the health pass waited seven and a half minutes on a pooled connection whose first command ignored its cancellation.

After three passes and at least 20 seconds, with the secondaries reporting that they are disconnected, the operator fences the primary. It deletes the pod with a grace period capped at 30 seconds, since a frozen process answers neither the `preStop` hook nor `SIGTERM`, waits for the kubelet to confirm that the containers are stopped, and promotes the most advanced secondary. The old primary is demoted when its pod comes back.

The events are `PrimaryUnreachable` (with the pass count), `AutoFailoverStarted`, `FencingInProgress`, `ReplicaFenced`, `FailoverCompleted`, then `ResurrectedPrimaryDemoted`. Writes stopped for 96.8 to 104.3 seconds, with no data loss, and all replicas were synchronized again about 45 seconds after the promotion.

### Primary isolated from everything

When a partition isolates the primary from the operator, its secondaries and its clients, the sequence is the same as for a frozen primary. The kubelet's TCP probes also fail, so the kubelet restarts the isolated container a few minutes later, and the operator demotes it when it comes back. Writes stopped for 83 to 120 seconds, with no data loss.

In some runs, the old primary stayed `RESOLVING` for about two minutes after its restart before taking the secondary role: the new primary's first connection to the new pod address timed out, and SQL Server waited before reconnecting. In other runs the role changed within about 30 seconds. This delays the return of redundancy, not writes.

### Primary cut off from its secondaries only

When port 5022 is blocked between the primary and its secondaries while the operator and the clients still reach the primary, the secondaries become `DISCONNECTED` within the 10-second session timeout. Commits then stop, since the primary cannot harden them on a secondary. The operator sees a healthy primary and does not fail over. The next step depends on the durability profile:

- With `Quorum`, writes stay blocked for the duration of the partition and the operator publishes `DurabilityFloorHoldingWrites`. When the partition ends, the secondaries resynchronize and writes resume. No data was lost in the campaign.
- With `Legacy`, the operator lowers `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` to 0 (`RequiredSyncLowered`) and writes resume without a copy. The value is restored when a secondary is synchronized again (`RequiredSyncRestored`). Writes stopped for 17 to 19 seconds, and the secondaries resynchronized within 20 seconds after the partition ended.

### Primary unreachable from the operator only

When a network policy, a DNS failure or a saturated operator pod cuts the operator off from the primary while the rest works, the secondaries are still `CONNECTED` to the primary. The operator takes that as proof that the primary is alive, does not fail over, and publishes `PrimaryUnreachableFromOperator`. In the campaign, this lasted 3.4 minutes without any failover or failed write.

### Primary cut off from its secondaries but reachable by clients

This is the split-brain case: the primary loses the operator and its secondaries, while the applications still reach it.

When `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` is 1 (always with `Quorum`, and with `Legacy` until the operator lowers it), the isolated primary cannot acknowledge any commit, because no secondary can harden it. The operator fences it and promotes a secondary. Nothing written to the old primary was acknowledged, so nothing is lost. Writes stopped for 68 to 105 seconds, with no data loss, and the old primary accepted no commit during the partition.

With `Legacy`, the value may already be 0 when the operator loses the primary: the partition between replicas came first, the operator lowered the value, and the operator was cut off afterwards. The old primary then keeps committing alone, and its clients get successful acknowledgements. A promotion would discard those commits, so the operator neither fences nor promotes. It raises `NeedsAttention` with reason `NoLosslessFailoverTarget`, and the message states that the old primary may still be serving clients.

In that situation, check whether the old primary is still serving clients. If it is, restoring the network is enough for the AG to converge. If it is permanently lost, a [forced failover](#forced-failover-with-data-loss) restores writes and discards the commits the old primary made alone.

`FailoverRefusedUnknownPrimary` covers a related case: the status does not record which replica was the previous primary, so the operator cannot tell which replica may still be writing. It then promotes only if every other replica answers and none of them is primary.

### Return of the old primary

After a failover, the StatefulSet recreates the old primary's pod, and SQL Server starts with its local AG metadata, which still says `PRIMARY`. For 90 seconds after the failover, the operator probes it every second, takes it `OFFLINE` as soon as it answers as primary, then sets it to secondary. The writer label is never put back on it, so clients do not reach it through the listener.

When two primaries are detected and they are not the two ends of the last recorded failover, the operator removes every writer label and raises `NeedsAttention` with reason `SplitBrain`. The message gives the replica names and the commands to resolve the situation. Decide which replica holds the data to keep, set the other one to secondary manually, and the operator restores the labels.

### New primary lost right after its promotion

A primary killed one second after a planned failover came back as primary 15 seconds later, and the former primary resynchronized on its own: 16.4 seconds without writes, no data loss. When the primary was isolated and the newly promoted replica was then frozen, the operator promoted the third replica, again without loss, after 105.9 seconds without writes.

### Primary and a secondary lost together

The operator promotes a survivor only if it can still establish that the promotion is lossless, which requires every replica that was synchronized at the last observation to be reachable, and the candidate to hold the highest log. Otherwise it waits. In the campaign, the primary was frozen and a secondary deleted at the same time. The deleted secondary came back after about 20 seconds with the highest LSN and was promoted: 103 to 121 seconds without writes, no data loss.

## The secondaries

### Secondary lost

The primary keeps accepting writes, but a synchronous secondary that disappears without closing its connection blocks every commit until `SESSION_TIMEOUT` expires, about 10 seconds. This is the behaviour of synchronous commit in SQL Server. The session timeout can be set with `replicaDefaults.sessionTimeout` or per replica in `replicas.overrides`, with a minimum of 5 seconds accepted by SQL Server; lower values cause more spurious disconnections. The secondary is removed from the read-only listener at the next pass and resynchronizes when its pod returns. In the campaign, commits stalled for 10.1 seconds with no failed write, and the replica was synchronized again about 28 seconds after it came back.

### Two secondaries lost

With `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` at 1, commits wait until a secondary returns. With `Legacy`, the value is lowered to 0 if they stay away. When both pods were recreated at the same time, writes stopped for 14 to 15 seconds, until the first one came back.

### Secondary not synchronizing

A secondary can remain `NOT SYNCHRONIZING` or `SUSPENDED` after a failover, a crash or a network failure. The operator resumes it (`HADR RESUME`) up to five times in a row, then again after five minutes. After two rounds without effect, about ten minutes, it drops the secondary's copy and re-seeds it from the primary (`ReplicaReseeded`), at most once an hour per replica. Before dropping anything, it checks that the replica's volume can hold the new copy. If it cannot, nothing is dropped and the database raises `ReseedBlocked` with the space needed, which is resolved by growing the volume.

A replica that is `RESOLVING` or disconnected is not counted as stalled, since a resume cannot reach it. A replica being seeded is left alone for 30 minutes plus the time to copy the database at 20 MB/s.

A re-seed can also be requested manually:

```bash
kubectl annotate sagdb <name> -n <ns> mssql-operator.evariops.com/reseed-replica=<replica>
```

The annotation is consumed immediately. A re-seed that fails publishes `ReseedFailed`; setting the annotation again retries it.

### Stuck seeding

When the primary fails during a re-seed, the new primary can keep the partial copy in a seeding state that never ends, and requesting automatic seeding again has no effect. Once the seeding exceeds its time bound (30 minutes plus the size at 20 MB/s), the operator treats it as stuck. The next re-seed first cancels it, by switching the replica's seeding mode to `MANUAL` and back to `AUTOMATIC`, then seeds again.

### Disconnected secondary holding the log

The primary keeps the log records that a secondary has not received, so a secondary that stays disconnected makes the log grow until the volume is full, at which point every write fails. When a replica has been disconnected for `failover.removeDisconnectedReplicaAfterMinutes` (30) and the log is at least 85 % full, the operator removes it from the AG, which releases the log. When the replica returns, the operator adds it back and re-seeds it.

### Secondary with lost volumes

When the PVCs of a secondary are deleted or its local storage is lost, the StatefulSet recreates the volumes empty, at the size of the template. The operator grows them to the size of the peers' volumes, then re-provisions the replica: new endpoint certificate, trust updated on the peers, join, seeding. In the campaign, the replica was synchronized eleven seconds after the re-provisioning started, with no failed write.

## Nodes, storage and resources

### A node is lost

When a node stops responding (powered off or partitioned), Kubernetes does not recreate StatefulSet pods from it, because it waits for a confirmation from a kubelet that does not respond. The pod stays `Terminating` or not ready.

A secondary on that node leaves the AG running with one replica less. For a primary on that node, the operator fails over once it has established that the old primary can no longer write, by deleting the VolumeAttachments of its volumes and confirming that they are gone. With storage that does not use VolumeAttachments (NFS, local-path), it cannot establish this, and it refuses the failover (`FailoverAborted`) until the steps below are taken.

The [breakout controller](architecture/availability.md#lost-nodes) relocates the pod automatically only when the node's Lease has not been renewed for 10 minutes (`breakout.relocationDelaySeconds`) and every volume of the pod uses a CSI driver listed as trusted, meaning that its detach removes the node's access to the data. Otherwise it publishes `RelocationRefused`, and the owner raises `NeedsAttention` with reason `ReplicaOnLostNode` (or `InstanceOnLostNode` for a `SqlInstance`). The procedure is then:

1. If the node can come back, restart it. The pod starts again on it and nothing else is needed.
2. Otherwise, confirm that the node is really down: machine powered off, or deleted by the provider. This confirmation is what the operator could not obtain; moving the pod while the old node still runs could let two processes write to the same data.
3. Declare the node out of service. Kubernetes then detaches its volumes and deletes its pods:

   ```bash
   kubectl taint node <node> node.kubernetes.io/out-of-service=nodeshutdown:NoExecute
   # once the pod runs elsewhere and the node is repaired or deleted:
   kubectl taint node <node> node.kubernetes.io/out-of-service-
   ```

4. If the volumes were local to the node, their data is lost. For an AG replica, delete its PVCs; the operator recreates them and re-seeds the replica. For a `SqlInstance`, restore from backups.

Relocation becomes automatic once the storage driver is declared as trusted in the Helm values. Only do this for a driver whose detach actually removes the node's access, such as network block storage with `attachRequired: true`:

```yaml
breakout:
  trustedFencingCsiDrivers: [ebs.csi.aws.com]
  relocationDelaySeconds: 600   # time the node stays NotReady before the relocation
```

### All replicas restarted

When the three SQL Server pods and the two operator pods were deleted together, replica 0 came back as primary and no failover took place: 31 to 42 seconds without writes, no data loss. When the only node hosting the three replicas rebooted, the operator reached no replica and took no action; its event stated that the replica pods were not running. The AG came back with its original primary when the node did, after 188 seconds without writes and with no data loss.

### Volume full

Data and log volumes are grown online at 80 % usage, up to four times their declared size, when the StorageClass has `allowVolumeExpansion: true`. A volume that cannot grow raises `VolumeFull` with `NeedsAttention`; add capacity or raise the declared size in the spec.

### Transaction log full

A long transaction, a lagging replica or a stopped log backup job can fill the transaction log, after which every write fails with error 9002. With `logBackup.mode: Local`, the operator backs up the log when it is half full and no other backup truncated it, so a stopped backup job no longer fills the log; in the campaign, stopping the log backup job under load caused no failed write. A log that fills anyway raises `TransactionLogFull` with `NeedsAttention`, and the message gives `log_reuse_wait_desc`, which identifies what holds the log (an open transaction, a replica).

Before this condition existed, a full log stopped every write for three minutes while the AG reported `Running` and the database `Synchronized`.

### SQL Server stops when its cpuset changes

The `mssql` container restarts without an OOM kill and without a failed probe. The end of its previous output (`kubectl logs <pod> -c mssql --previous`) shows:

```
This program has encountered a fatal error and cannot continue running
         Signal: SIGABRT - Aborted (6)
     Last errno: 22
Last errno text: Invalid argument
```

A `core.sqlservr.*.txt` file is left in `/var/opt/mssql/data`, and the error log contains nothing. Several replicas on the same node often stop together, a few seconds after another pod on that node stopped or started, so an AG can lose its primary and its secondaries at once.

SQL Server on Linux does not survive the removal of a CPU from its cpuset while it runs. Two components remove CPUs from running containers. An NRI resource policy plugin (`nri-resource-policy-balloons`, `topology-aware`) shrinks a balloon sized on the sum of CPU requests when one of its pods stops. The kubelet static CPU manager shrinks the shared pool of Burstable pods when a Guaranteed pod with whole CPUs gets exclusive CPUs. Each crash stops another container, which can shrink the balloon again, so the crashes cascade. In the second campaign, the primary and a secondary stopped together twice in a row during startup.

To confirm:

```bash
kubectl exec <pod> -c mssql -- cat /sys/fs/cgroup/cpuset.cpus.effective
# NRI balloons: resizes around the crash time
kubectl -n <nri-namespace> logs <nri-pod-of-the-node> --timestamps | grep -E "StopContainer|assigning container"
```

The crash follows a `StopContainer` of another pod in the same balloon by one or two seconds.

The fix is to give SQL Server a cpuset that does not change. With an NRI plugin, exclude its CPUs from the policy with pod annotations:

```yaml
spec:
  instanceTemplate:          # SqlAvailabilityGroup; at the root of spec on a SqlInstance
    podAnnotations:
      cpu.preserve.resource-policy.nri.io: "true"
      memory.preserve.resource-policy.nri.io: "true"
```

The plugin then logs `not handling resources of container …, preserving CPUs`. SQL Server sees every CPU of the node while the CPU limit still caps its usage, so set `max degree of parallelism` to the CPU limit with a [`SqlConfiguration`](reference/resources.md#sqlconfiguration). An alternative is a dedicated balloon type of fixed size (`minCPUs` equal to `maxCPUs`). With the static CPU manager, set `resources.cpuPinning: true` with whole CPUs (requests equal to limits): the pod becomes Guaranteed and its exclusive CPUs no longer change.

Changing the annotations triggers a rolling update. Until it completes, the replicas that have not restarted yet remain exposed, since every pod that leaves the balloon shrinks it.

### SQL Server 2025 does not start on a pinned cpuset

A SQL Server 2025 pod restarts in a loop and never listens on port 1433. Each attempt stops about two seconds after startup with:

```
This program has encountered a fatal error and cannot continue running
        Reason: 0x00000004
       Message: ASSERT: Expression=(NT_SUCCESS(status)) File=LibOS\Windows\Kernel\DRtl\sqlpalstubs.cpp Line=887
    Processors: 5
    Last errno: 22
Last errno text: Invalid argument
```

The crash handler then fails to capture a dump and prints permission errors on `/proc`, `/var/opt/mssql/log` and `dmesg`. These follow the crash and do not cause it; the root filesystem being read-only plays no part either.

Up to CU6-GDR1, SQL Server 2025 counts the CPUs of its cpuset, then pins its threads to that many of the node's first CPUs (`0 1 2 12 13` for a cpuset of five CPUs on a 24-CPU node) instead of the CPUs it was given. The kernel refuses an affinity that contains none of the allowed CPUs. Any cpuset that does not start with the node's first CPUs triggers it: the kubelet static CPU manager policy, an NRI resource policy plugin, or a `taskset`. The same pod starts when it sees every CPU of the node, and SQL Server 2022 runs on the same cpuset. CU7 maps the threads onto the allowed CPUs; CU7, CU8, CU9-GDR1 and CU10 were checked on cpusets of 1, 3, 5 and 7 CPUs, including SMT cores with one sibling outside the cpuset.

To confirm:

```bash
kubectl exec <pod> -c mssql -- grep Cpus_allowed_list /proc/self/status   # not 0-<n>
kubectl get pod <pod> -o jsonpath='{.spec.containers[0].image}'            # 2025-RTM … 2025-CU6-GDR1
```

The fix is CU7 or later: set `cumulativeUpdate` on the instance or on `instanceTemplate`. The webhook refuses SQL Server 2025 builds older than CU7 on new resources and on build changes. A resource created before this check keeps its build, with a warning on each change. For a resource that pins no `cumulativeUpdate` and was created on an operator whose default was CU6, approve the new default with `approve-engine-upgrade` ([SQL Server builds](guide/operating.md#sql-server-builds)).

### SQL Server out of memory

During the database campaign, the primary ran out of memory twice shortly after seeding a 4.5 GB database, with about 2.5 GB allocated outside SQL Server's memory manager. The AG had no `memory` block, so neither `memorylimitmb` nor `max server memory` was set, and SQL Server used its default of 80 % of the container limit. In both cases, the operator waited while the secondaries still reported a connected primary, then failed over without data loss. Setting `memory.total` on instances and AG templates avoids this: the operator derives both limits from it and leaves room for allocations outside the buffer pool.

### Slow restarts with full-text indexes

SQL Server pods with full-text indexes can take a long time to become ready after a restart, or be overloaded right after it. At startup, SQL Server resumes interrupted full-text populations, applies the change-tracking backlog and merges index fragments, all with a cold cache, and the filter daemon (`fdhost`) uses memory outside `max server memory`. On SQL Server 2017 and 2019, an upgrade with an `upgrade_option` other than `Import` repopulates every catalog at the first start.

The operator reports running populations in `status.fullText` and with the condition `FullTextPopulationActive`. An AG rolling update waits while a population runs on the primary (`RollingUpdateWaitingFullText`), up to a timeout. On 2017 and 2019, `FullTextUpgradeOptionSafe=False` reports an `upgrade_option` other than `Import`; the operator does not change it. `max server memory` leaves headroom in the pod for `fdhost`.

Before a cumulative update on SQL Server 2017 or 2019, set the upgrade option on each server with full-text indexes (SQL Server 2022 and later removed this option):

```sql
EXEC sp_fulltext_service @action = 'upgrade_option';   -- 0 = Rebuild, 1 = Reset, 2 = Import
EXEC sp_fulltext_service 'upgrade_option', 2;          -- import the indexes instead of rebuilding them
```

Before deleting a pod or draining a node, check for running populations:

```sql
SELECT DB_NAME(database_id) AS db, population_type_description, status_description, start_time
FROM sys.dm_fts_index_population;
```

Wait for them to complete, or stop them with `ALTER FULLTEXT INDEX ON dbo.MyTable STOP POPULATION;`. Crawls can be throttled with the `sp_configure` options `ft crawl bandwidth (max)` and `ft notify bandwidth (max)` in `spConfigure.advanced` of a `SqlConfiguration`; a value of `0` means unlimited.

## The operator and the control plane

A healthy AG keeps serving without the operator. Without it, failovers and repairs do not happen.

### Leader lost during a failover

The failover intent and the fencing stage are persisted before their first effect. The next leader, elected about 15 seconds later, reads them and completes the operation. In the campaign, the primary was frozen and the operator leader deleted during the fencing: there was exactly one promotion and no data loss, with 117.7 seconds without writes.

### Operator absent when the primary fails

No other component promotes a replica, so nothing fails over while the operator is down. When it comes back, it decides from the current state. In the campaign, the operator was scaled to zero, the primary frozen, and the operator restored 71 seconds later; it made a lossless promotion during its first passes, for 183 seconds without writes in total.

### Repeated operator restarts

Each restart elects a new leader that re-reads the whole state, and no action is taken from cached data. Five deletions of the leader, 20 seconds apart, under load, caused no failover and no failed write.

### Operator cut off from the API server

The leader cannot renew its Lease, so it exits within seconds (`Lost leadership … another writer may be active`), since another pod may already be leading. Kubernetes restarts it and it rejoins the election. Writes were not affected, and the Lease was taken again within 3 seconds once the API server was reachable.

### Operator cut off from every SQL Server, or from DNS

When the operator reaches no replica, it neither fences nor promotes, since it has no candidate and no proof. Its event states that its own connectivity is the likely cause when the replica pods are ready, or that the pods are not running. In the campaign no write failed. A DNS outage alone had no effect: established connections were reused and the API server is reached by IP address. A SQL Server pod with a new IP address would be unreachable, which leads to the previous case.

### Paused leader resuming

A leader paused by the scheduler or a debugger and resumed after a new leader took over may still hold an open SQL connection. From safety level 1, each topology command carries the epoch of its author and is checked against a stamp in `msdb` on the replica, under an application lock, so SQL Server refuses commands from the resumed leader (error 51002). At level 0, only the exit on Lease loss protects against this, which leaves a short window.

### Webhook unavailable

With `webhook.failurePolicy: Fail` (default), no resource of the operator's kinds can be created or changed while no operator pod answers the webhook. Running SQL Servers are not affected. During an operator rollout, a write can occasionally fail with `EOF` from the webhook while an old pod stops; retrying the write is enough. The view API is served by the same pods, and while it is unavailable, namespace deletions are delayed across the cluster because the namespace controller waits for every aggregated API. Two operator replicas (the default) avoid this.

### etcd restored from an old snapshot, or control record lost

A restore can move the epoch Lease and the control records back in time. The epoch stamps in each replica's `msdb` are not affected, and a leader that finds a stamp above its own epoch raises its epoch above it before acting. A missing control record is rebuilt from SQL Server and from the status and marked with `reconstructedAt`, and no destructive action runs until the rebuilt record is written.

## Changes made by people and GitOps tools

### Resource deleted and re-created

A GitOps tool may delete and re-create a resource, which gives it a new UID and an empty status. The operator keeps ownership information outside the resource: in SQL Server (login ownership, original settings, database ownership), in the control record (epoch, durability profile, divergences) and in key Secrets without owner references. The re-created resource takes back what its predecessor owned. A `SqlUser` re-created with fewer roles revokes the others on every replica, and a `SqlConfiguration` re-created without a trace flag disables it.

### Key Secret deleted or replaced

A deleted key Secret, such as an SMK backup, is detected by the watch and recreated from SQL Server at the next reconcile. Key Secrets are immutable and cannot be edited in place.

A replaced machine key Secret would prevent SQL Server from decrypting its service master key at the next restart. The operator compares the Secret with the fingerprint stored in the control record and raises `NeedsAttention` with reason `MachineKeyChanged`. Restore the original Secret from a copy. If the service master key itself has to be restored, follow [Restoring the service master key](guide/protecting-data.md#restoring-the-service-master-key).

### sa password rotation

After the Secret is updated, the operator applies the new password on every replica (`SaPasswordRotated`) and keeps the previous ones in `<name>-sa-applied`, so that it can still log in where the rotation has not been applied yet. A replica that refuses the operator's login is alive, and is never counted as a lost primary. On three replicas, the rotation converged in 2 seconds without any failover. Applications that read the password from an environment variable must be restarted to use the new one.

### Planned failover to a replica that is not ready

`failover-to` on a replica that is not synchronized is rejected (`FailoverRejected`) and the annotation is removed. A failover to a synchronized target that fails is retried up to three times, then abandoned (`FailoverAbandoned`). A request for the current primary does nothing (`FailoverNoop`).

### Forced failover with data loss

`force-failover-to` with `allow-data-loss=true` promotes the target regardless of the commits it lacks. Both annotations are removed before the failover runs, so it cannot be replayed. The failover is recorded with `dataLoss: true`, the divergence is recorded in the control record and in each database, and the replicas that may hold the lost commits do not rejoin automatically (`ManualRejoinRequired`). These are the old primary and any replica that synchronized from it after the failover point.

In the campaign, the AG used `Legacy` durability and `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` had been lowered to 0 before the operator was cut off. The forced failover lost the 443 commits that the old primary had acknowledged alone during the previous 59 seconds.

If the lost commits are needed, recover them from a divergent replica first. Then allow the replicas to rejoin; their extra commits are discarded:

```bash
kubectl annotate sagdb <name> -n <ns> mssql-operator.evariops.com/approve-data-loss-resume=true
```

The approval is removed once used, so it does not apply to a later failover.

### Failure during Plan mode

In `Plan` or `Observe` mode, the operator records its decisions and runs no disruptive action: no fencing, no demotion, no promotion. A frozen primary stays as it is, and `kubectl get operatorplans` shows the failover the operator would run. Switching back to `Apply` runs it. Requests made by annotation in the meantime wait and are not counted as failed attempts.

## Databases

### Failover between the creation of a database and its join

When a failover happens after a `SqlDatabase` was created on the primary and before the database joined the AG, the copy left on the former primary is empty. The operator backs it up to `/var/opt/mssql/backup/mssql-operator/<database>_orphan_<date>.bak`, drops it (`OrphanCopyDropped`) and creates the database again on the new primary. In the campaign, the database was synchronized on the three replicas without intervention.

### Database with data left on a former primary

An adopted database, or a database used while its join waited for a backup window, can be left on a former primary by a failover. The operator never drops it. The `SqlDatabase` goes to phase `Failed` with reason `DatabaseOnAFormerPrimary`, and nothing is created on the new primary. A planned failover back to that replica (`failover-to`) lets the join complete. In the campaign, a 4.5 GB database left this way during its initialization backup stayed intact and joined after the failback.

### Re-seed requested during a seeding

When a re-seed is requested while a seeding is in progress, the copy being seeded can disappear before the drop. The operator treats that as already dropped and starts the seeding again. Any other failure of a requested re-seed publishes `ReseedFailed`.

## Out of scope

The loss of the whole cluster, the loss of every replica's storage, corruption replicated by SQL Server and deletions made by applications all require backups, which the operator does not schedule. See [Backups and encryption](guide/protecting-data.md) and [Limitations](limitations.md).
