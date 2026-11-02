# Limitations

This page describes what the operator does not do, does only partially, or has not been validated for, together with the reasons and the available workarounds.

## High availability

The duration of a failover is dominated by detection. The operator considers a primary lost only after three consecutive health passes and at least 20 seconds without an answer, so that a short network interruption or a slow probe does not trigger a promotion. This 20-second floor cannot be lowered. Fencing and the role change come on top of it, which put failovers after a hard failure of the primary (frozen process, network isolation) at 80 to 120 seconds in the chaos campaigns.

Synchronous commit has a cost that the operator cannot remove. When a synchronous secondary disappears without closing its connection, SQL Server holds every commit until the session timeout expires, 10 seconds by default. The rolling update avoids this pause by suspending the data movement of a secondary before deleting its pod, but a secondary that crashes or is cut off from the network causes it every time.

Failing over a primary that was lost together with its node depends on the storage. The only evidence that such a primary can no longer write is the removal of its VolumeAttachments, so the operator fails over automatically only when the storage uses them. With NFS, local-path or hostPath volumes, this failover waits for a human ([A node is lost](failure-modes.md#a-node-is-lost)). For the same reason, the breakout controller relocates a pod from a lost node only when its CSI driver is declared as trusted. That list is set with an environment variable on the operator Deployment, which the chart does not expose yet, and it is empty by default, so relocation is manual unless it is configured.

`Legacy` durability trades data safety for write availability. Once `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` has been lowered to 0, the primary acknowledges commits that exist nowhere else, and a failover at that point loses them. The operator therefore does not fail over automatically in that state unless `allowAutomaticDataLossFailover` is set. Standard edition AGs, which are limited to two replicas, can only use `Legacy`, and run at 0 since SQL Server does not support the setting on a basic availability group: their automatic failover requires `allowAutomaticDataLossFailover`.

A node maintenance leaves some topologies with a single copy of the data while the node reboots, whatever the operator does: a Standard AG and a `Legacy` AG with one synchronous secondary keep writing on the primary alone, and a `Quorum` AG with one synchronous secondary blocks the writes until the secondary is back, as its profile requires. Only three replicas, two of them synchronous, keep both the writes and a second copy through a maintenance ([Node maintenance](guide/node-maintenance.md)). The eviction webhook delays a replica within a deadline (210 s for the drain of `talosctl upgrade`, 600 s otherwise) and never blocks a drain: past the deadline, or when the operator is not running, evictions go on as they would without it. Its first answer to each eviction is a refusal while the leader computes the verdict, which costs the drain one retry (5 s).

After a failover, redundancy can take longer to come back than writes. The old primary sometimes stays `RESOLVING` for about two minutes before becoming a secondary, because SQL Server waits before reconnecting to a pod whose address changed. This happens intermittently and has no effect on writes.

The topology of an AG is fixed at creation: `spec.replicas.count` and the endpoint port are immutable, and changing the number of replicas requires a new AG. An AG also lives in a single Kubernetes cluster; distributed availability groups and replicas in other clusters are not supported, so there is no disaster recovery site beyond backups.

Safety level 2 is not safe to enforce. Its correctness relies on a timing inequality between the quorum watchdog and the election delay, and the SQL Agent behaviour measured during a partition breaks it by about one second ([Safety mechanisms](architecture/safety.md#level-2-protocol-experimental)). Safety level 3 has not been through a chaos campaign. The recommended ceiling is 1.

Finally, the chaos campaigns had blind spots. The three replicas shared one node and used local storage, so a node failure was always a total outage rather than a failover, and fencing by VolumeAttachment removal is covered by unit tests but was not exercised on real hardware.

## Backups and keys

The operator does not schedule full or differential backups, and it does not manage restores, including point-in-time restores. It only takes the backups that availability groups need to work: the initialization backup of a database joining an AG, and log backups when no other tool takes them. Retention, backup targets and restore tests remain the responsibility of the existing backup tooling.

The keys that make the data readable (machine keys, service master key backups, TDE certificates) are stored as Kubernetes Secrets, in etcd. If the cluster is lost without a copy of these Secrets, the data on the volumes cannot be decrypted, even if the volumes themselves survive. Copying them outside the cluster is not automated.

Restoring a service master key is a manual procedure ([Restoring the service master key](guide/protecting-data.md#restoring-the-service-master-key)), because `RESTORE SERVICE MASTER KEY` reads a file from the pod filesystem and SQL Server on Linux cannot write that file from a session. Superseded TDE certificates are never dropped, since backups taken under them may still need them, and they have to be dropped manually once no backup in the retention period depends on them.

## Operations

The operator connects to SQL Server as `sa`. A dedicated least-privileged login is not implemented; only stored procedures are deployed through a restricted login. In addition, with the default `sqlConnection.trustServerCertificate: true`, the connection is encrypted but the server certificate is not verified, so the operator does not authenticate the SQL Servers it connects to until they have verifiable certificates.

The admission webhook and the view API are served by the operator pods, so their availability follows the operator's. With the default `webhook.failurePolicy: Fail`, no resource of the operator's kinds can be created or changed while no operator pod answers, and an unavailable aggregated API delays namespace deletions across the cluster. During an operator rollout, a write can also fail with `EOF`: the operator pods have no `preStop` delay, so a pod that is stopping can still receive webhook calls. Retrying the write is enough.

`kubectl get operatorplans` shows the decisions held in the current leader's memory. A restart of the leader empties the list; the corresponding events remain.

Memory limits are only set when they are declared. Without `memory.total`, the operator sets neither `memorylimitmb` nor `max server memory`, and SQL Server sizes itself at 80 % of the container limit. Memory that SQL Server allocates outside its buffer pool can then get the container killed, which happened twice in the database campaign.

A few behaviours are deliberately left to the user. `SqlConfiguration` options that take effect only at startup are reported with `PendingRestart`, but the operator does not restart SQL Server to apply them. With a private image mirror (`image`), the operator cannot read the build from the tag, so downgrades and unpublished builds are no longer detected.

The operator watches the whole cluster with a ClusterRole, and there is no namespace-scoped installation.

Scale has not been measured. The operator reads objects from the API server on each reconcile, without a shared cache, and the test clusters ran a few AGs. The infrastructure part of the AG reconcile was already moved to a one-minute cadence after it was found to slow down the health pass beyond a few dozen AGs. With many AGs, `mssqloperator_workqueue_oldest_age_seconds` and `mssqloperator_ag_last_health_check_timestamp_seconds` show whether the health pass keeps its 10-second cadence.

## API

The API is `v1alpha1` and can change. There is no conversion webhook, so a breaking change requires exporting, rewriting and re-applying the resources; this was the case for the change of API group. The CRD schemas are maintained by hand, alongside the code.

## SQL Server coverage

The operator runs SQL Server 2019, 2022 and 2025 from the Linux images of `mcr.microsoft.com/mssql/server`, in the Developer, Express, Standard and Enterprise editions. Windows and failover cluster instances are not supported. `SqlUser` manages SQL logins with passwords; Windows, Active Directory and Entra ID logins are not managed.

Features without a corresponding resource are neither managed nor blocked. This includes:

- replication and log shipping;
- linked servers;
- Resource Governor;
- Integration, Reporting and Analysis Services.

## Security

The operator reads Secrets in every namespace and connects as `sa` to every server it manages, so whoever controls the operator controls all of those servers. Its metrics are served over plain HTTP and must stay inside the cluster. On the nodes, a root user can read the memory and the volumes of the SQL Server pods; nothing in the operator protects against that. The reasoning behind these trade-offs is in [Security model](architecture/security.md).
