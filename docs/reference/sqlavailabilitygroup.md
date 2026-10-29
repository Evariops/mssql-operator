# SqlAvailabilityGroup

An Always On availability group: N SQL Server replicas in one StatefulSet, the AG between them, and
Services to reach the primary and the readable secondaries. The AG runs without a cluster manager
(`CLUSTER_TYPE = NONE`): the operator decides failovers. Databases are declared separately with
[`SqlAvailabilityGroupDatabase`](sqlavailabilitygroupdatabase.md).

How to run one day to day: [Availability groups guide](../guides/availability-groups.md).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: orders
  namespace: sql
spec:
  edition: Enterprise            # Enterprise (default), Developer, or Standard (exactly 2 replicas)
  replicas:
    count: 3
    synchronousCount: 3          # replicas in synchronous commit, primary included
    durability: Quorum           # no acknowledged commit lost by a failover; needs synchronousCount >= 3
  saPasswordSecretRef: { name: orders-sa, key: password }
  masterKeyPasswordSecretRef: { name: orders-dmk, key: password }
  hadrCertificate: { source: Generate, model: PerReplica }
  instanceTemplate:
    version: "2022"
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

The two Secrets must exist in the same namespace, each with a `password` key.

## What gets created

| Object | Name | Purpose |
|---|---|---|
| StatefulSet | `<name>` | the replicas, pods `<name>-0` … `<name>-N` |
| Service | `<name>-listener` | read/write: always points at the primary |
| Service | `<name>-ro` | read-only: readable synchronized secondaries (the primary when none, by default) |
| Service | `<name>-headless` | stable DNS name per replica, used by the AG endpoints |
| PodDisruptionBudget | `<name>-pdb` | at most one replica evicted at a time |

## Main fields

| Field | Default | What it does |
|---|---|---|
| `edition` | `Enterprise` | `Enterprise`, `Developer` (2–9 replicas) or `Standard` (exactly 2, no readable secondary). |
| `replicas.count` | `3` | Number of replicas. |
| `replicas.synchronousCount` | `2` | Replicas in synchronous commit, the primary included. Only these are automatic failover targets. |
| `replicas.durability` | see text | `Quorum`: every commit is hardened on a secondary; writes block rather than lose durability (needs `synchronousCount` ≥ 3). `Legacy`: writes keep flowing when secondaries drop, at the price of possible data loss on failover. Unset: `Quorum` for a new Enterprise AG of 3+ replicas, `Legacy` otherwise. Set it explicitly. |
| `saPasswordSecretRef` | required | `sa` password for every replica. |
| `masterKeyPasswordSecretRef` | required | Database master key password (endpoint certificates, TDE). |
| `hadrCertificate` | required | Endpoint authentication. Only `source: Generate`, `model: PerReplica` is implemented. |
| `instanceTemplate.version` | `2022` | SQL Server major version. |
| `instanceTemplate.cumulativeUpdate` | operator default | Exact build every replica runs (`CU25`, `CU32-GDR1`). Changing it rolls the AG. |
| `instanceTemplate.memory.total` | | Pod memory; SQL Server `max server memory` is derived from it. |
| `instanceTemplate.storage.<volume>` | `10Gi` | `data`, `log`, `backup`, and optional `sysDb`, `tempDb`, `errorLogs`, `certBackups`. Can only grow. |
| `instanceTemplate.tls.secretName` | | Certificate the replicas present to clients. See [Security](../guides/security.md). |
| `instanceTemplate.podAnnotations` | | Extra pod annotations (for example the NRI ones in the [cpuset runbook](../runbooks/cpuset-changes.md)). |
| `listener` / `readOnlyListener` | | Service type, port and annotations of the two Services. `readOnlyListener` is Enterprise only. |
| `readOnlyRouting.enabled` | `false` | SQL Server read-only routing for `ApplicationIntent=ReadOnly` clients. |
| `failover.allowAutomaticDataLossFailover` | `false` | Let the operator force a failover that may lose data when no synchronized secondary is left. |
| `failover.primaryUnreachableThreshold` | `3` | Health passes (10 s apart) without a primary before the operator fails over. |
| `failover.removeDisconnectedReplicaAfterMinutes` | `30` | Remove a replica that holds the transaction log back (log 85 % full); it is added back when it returns. `0` disables. |
| `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` | `60` | Pause between two replicas during a rolling update. |
| `volumeAutoGrow.mode` | `Auto` | Grow data and log volumes online when 80 % full, up to 4 × their declared size. |
| `topologySpread` / `antiAffinity` | spread over nodes and zones (preferred) | Placement of the replicas. |
| `networkPolicy` | off | A CiliumNetworkPolicy: who may reach 1433, and which destinations the pods may reach. |
| `automation.mode` | `Apply` | `Observe`, `Plan` or `Apply`. See [Automation](../guides/automation.md). |

Every other field: `kubectl explain sqlavailabilitygroup.spec --recursive`.

## Annotations

| Annotation | Effect |
|---|---|
| `mssql-operator.evariops.com/failover-to: <replica>` | Planned, lossless failover to that replica. Consumed when done. |
| `mssql-operator.evariops.com/force-failover-to: <replica>` + `allow-data-loss: "true"` | Forced failover, data loss accepted. |
| `mssql-operator.evariops.com/approve: <action id>` | Lets a withheld destructive action run (ids in `kubectl get operatorplans`). |
| `mssql-operator.evariops.com/retry-rolling-update: "true"` | Restarts a rolling update that stopped in `Failed`, once the cause is fixed. |
| `mssql-operator.evariops.com/approve-engine-upgrade: "true"` | Applies a new default cumulative update that arrived with an operator upgrade. |
| `mssql-operator.evariops.com/safety-level: "1"` | Safety level of this AG (capped by the operator setting). See [Automation](../guides/automation.md#safety-levels). |

## Status

```console
$ kubectl get sqlavailabilitygroup -n sql
NAME     PHASE     PRIMARY    CU     BUILD          AGE
orders   Running   orders-0   CU25   16.0.4195.2    3d
```

- `status.phase`: `Pending`, `Initializing`, `Running`, `Degraded`, `FailingOver`, `Failed`.
- `status.replicas[]`: role, availability mode, synchronization and connection state of each replica.
- `status.lastFailover`: when, from, to, why, and whether data was lost.
- `status.conditions`: watch **`NeedsAttention`**. It is `True` only when a human decision is needed
  (split brain, no lossless failover target, rolling update failed, volume full), and its message
  says what to decide. `DurabilityAtRisk` says the AG runs without a durability guarantee.

Live replica health (lag, synchronization per database): `kubectl get agreplicastates -n sql`.
