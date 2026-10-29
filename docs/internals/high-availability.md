# High availability internals

How the operator keeps an availability group writable without losing acknowledged commits. Usage:
[Availability groups guide](../guides/availability-groups.md).

## Why the operator is the cluster manager

The AG is created with `CLUSTER_TYPE = NONE`: SQL Server never fails over on its own. In this mode
the only promotion command is `FORCE_FAILOVER_ALLOW_DATA_LOSS`, lossless or not depending on the state
of the target. Everything that makes a failover safe is therefore the operator's job: choosing a
target that has every committed transaction, and making sure the old primary can no longer write.

## Health

Every 10 seconds the AG supervisor reads, on every replica, its role and the synchronization state of
each database (`sys.dm_hadr_*`). The primary's view is authoritative for the secondaries' states. A
change wakes the AG controller at once.

## Failover

1. **Detection.** No replica answers as primary for `primaryUnreachableThreshold` passes (3). If the
   secondaries still see a connected primary, only the operator is cut off: no failover.
2. **Target.** A synchronized replica in synchronous commit, the one with the highest hardened LSN
   on every database. Never an older SQL Server build than the last primary (it cannot redo its log).
3. **Isolation of the old primary**, before any promotion:
   - reachable: `SET (ROLE = SECONDARY)`, verified;
   - otherwise: its pod is deleted gracefully and the kubelet must confirm it is gone; on a node that
     does not answer, every VolumeAttachment of its volumes is deleted and must disappear;
   - if neither can be proven, no promotion (`FailoverAborted`).
4. **Promotion** of the target, then the listener labels move.
5. **Resurrection.** The old primary's pod restarts and its SQL Server comes back believing it is
   primary. For 90 seconds the AG is reconciled every second, probing it; it is taken `OFFLINE` as soon
   as it answers as primary, then demoted to secondary.

The operation is recorded in the [control record](../reference/sqlcontrolrecord.md) before its first
effect, so an operator restarted in the middle resumes or abandons it cleanly.

## Durability

`REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` (k) decides when a commit is acknowledged.

- **Quorum**: k never goes below 1. A commit is acknowledged only once it is hardened on a
  synchronized secondary, so any synchronized secondary holds every acknowledged commit and a
  failover to it loses nothing. Without a synchronized secondary, writes block.
- **Legacy**: k may be lowered to 0 when synchronous secondaries drop, to keep writes flowing. A
  failover can then lose up to the session timeout plus a few seconds of writes, which is why an
  unplanned one is automatic only with `allowAutomaticDataLossFailover`.

The profile is fixed in the control record once reached; an existing AG moving to Quorum raises its
replicas to synchronous commit one at a time.

## Safety level 2: quorum watchdog and elections (experimental)

Level 2 adds protection for a partition that cuts the primary from its secondaries while the operator
cannot tell which side is alive.

- **Quorum watchdog.** A SQL Agent job `mssqlop-watchdog-<ag>` runs on every replica every 10 seconds.
  On the primary, when fewer than k secondaries have been synchronized for T_step (20 s), it takes the
  AG `OFFLINE` itself, without the operator. In shadow it only records what it would do
  (`msdb.mssqlop.watchdog`).
- **Elections.** A secondary is promoted only when no reachable secondary still sees the primary,
  enough secondaries are isolated that no acknowledged commit can be missing from all of them, and the
  old primary is provably out (offline, dead, or its watchdog alive and lost by every secondary for
  T_vote = 45 s).

Measured during the chaos campaign: during a partition, each watchdog run takes about 6 s (SQL Agent
resolves DNS before each step) and runs come every 20 s instead of 10. The worst-case step-down then
reaches about 46 s, against T_vote = 45 s: the election margin is gone. Before enforcing level 2,
T_vote must be raised (about 60 s) or the watchdog moved out of SQL Agent, and the simulation re-run
with a 20 s period. Keep the ceiling at level 1 until then.

## Lost nodes

A StatefulSet pod on a node that stopped answering is never recreated by Kubernetes. The breakout
controller relocates it after 10 minutes when its storage driver is trusted to cut the node's access
on detach; otherwise it raises `NeedsAttention` and a human decides
([runbook](../runbooks/lost-node.md)). An AG keeps serving without that replica.
