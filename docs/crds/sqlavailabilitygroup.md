# `SqlAvailabilityGroup`

> Declaratively provisions a SQL Server Always On Availability Group across a set of replicas, with operator-orchestrated failover, listeners and read routing.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlAvailabilityGroup` · scope: Namespaced · plural: `sqlavailabilitygroups`

## Minimal example

The smallest manifest the webhook accepts that produces a working AG. `edition` defaults to `Enterprise`. The two password Secrets and `hadrCertificate` (`source: Generate`, `model: PerReplica`) are required.

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mssql-sa-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "YourStr0ngP@ssw0rd!"
---
apiVersion: v1
kind: Secret
metadata:
  name: mssql-master-key-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "An0therStr0ngP@ss!"
---
apiVersion: mssql.operator/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: my-ag
  namespace: mssql-ag-demo
spec:
  replicas:
    count: 3
    synchronousCount: 2
  saPasswordSecretRef:
    name: mssql-sa-password
    key: password
  masterKeyPasswordSecretRef:
    name: mssql-master-key-password
    key: password
  hadrCertificate:
    source: Generate
    model: PerReplica
  instanceTemplate:
    version: "2022"
    storage:
      data:
        size: 20Gi
      log:
        size: 10Gi
```

## Overview

A `SqlAvailabilityGroup` stands up N SQL Server replicas (a StatefulSet), bootstraps a clusterless Always On Availability Group between them, and exposes a read/write listener (and optionally a read-only listener) as Kubernetes Services. The AG always runs with `CLUSTER_TYPE = NONE` and `FAILOVER_MODE = MANUAL`. There is no Windows/Pacemaker cluster, so the operator itself owns all orchestration. The operator manages HADR certificate exchange, role and readability labels, failover (including optional split-brain fencing), read-only routing and AG-aware rolling updates. Databases are not declared here. They are decoupled into separate `SqlAvailabilityGroupDatabase` resources that reference this AG.

## Spec

### Top-level fields

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `edition` | string | No | `Enterprise` | SQL Server edition. Allowed: `Standard`, `Enterprise`, `Developer`. Determines available features and replica constraints. |
| `replicas` | object | No | (see below) | Replica topology (count, sync mode, per-ordinal overrides). |
| `replicaDefaults` | object | No | (see below) | Default per-replica settings applied unless overridden. |
| `instanceTemplate` | object | No |   | SQL Server instance template (image, resources, storage, scheduling, security). |
| `hadrCertificate` | object | **Yes** |   | HADR (X.509) certificate config for endpoint authentication. Required by the webhook. |
| `endpoint` | object | No | (see below) | Database mirroring endpoint configuration. |
| `masterKeyPasswordSecretRef` | object (`SecretReference`) | **Yes** |   | Secret holding the Database Master Key password. Required by the webhook. |
| `saPasswordSecretRef` | object (`SecretReference`) | **Yes** |   | Secret holding the SA password applied to all replicas. Required by the webhook. |
| `machineKeySecretRef` | object (`SecretReference`) | No |   | Secret holding a machine key for all replicas (migration scenario). |
| `smkBackupPasswordSecretRef` | object (`SecretReference`) | No |   | Secret holding the SMK (Service Master Key) backup encryption password. |
| `operatorLoginSecretRef` | object (`SqlLoginSecretReference`) | No |   | Optional dedicated SQL login the operator uses instead of SA. |
| `listener` | object | No |   | Read/Write listener service. |
| `readOnlyListener` | object | No |   | Read-Only listener service. Enterprise only, rejected for Standard. |
| `readOnlyRouting` | object | No |   | Read-only routing configuration. Enterprise only, cannot be enabled on Standard. |
| `failover` | object | No | (see below) | Failover behaviour and split-brain protection. |
| `updateStrategy` | object | No |   | AG-aware rolling update strategy. |
| `antiAffinity` | object | No | preferred hostname spread | Pod anti-affinity rules. |
| `networkPolicy` | object | No |   | CiliumNetworkPolicy configuration for SQL + mirroring ports. |
| `backupPreference` | string | No |   | Enterprise only: `AUTOMATED_BACKUP_PREFERENCE`. Allowed: `Primary`, `Secondary`, `Prefer_Primary`, `None`. Rejected for Standard. |

### `replicas` (AgReplicaTopologySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `replicas.count` | int | No | `3` | Total number of replicas. Standard: must be exactly `2`. Enterprise/Developer: `2` to `9`. |
| `replicas.synchronousCount` | int | No | `2` | Number of replicas in `SYNCHRONOUS_COMMIT` mode (including the primary). Cannot exceed `count`. |
| `replicas.requiredSynchronizedSecondariesToCommit` | int | No | `1` | Synchronized secondaries required before commit returns (`REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT`). `0` = read-scale, `1+` = data protection. Must be `< synchronousCount`. |
| `replicas.overrides[]` | array | No |   | Per-replica overrides keyed by ordinal. |
| `replicas.overrides[].ordinal` | int | Yes (in item) |   | Pod ordinal index (0-based). |
| `replicas.overrides[].availabilityMode` | string | No |   | `SynchronousCommit` or `AsynchronousCommit`. (Async rejected for Standard.) |
| `replicas.overrides[].readableSecondary` | string | No |   | Enterprise only: `No`, `ReadOnly`, or `All`. (Standard only allows `No`.) |
| `replicas.overrides[].backupPriority` | int | No |   | Enterprise only: backup priority `0` to `100`. (Rejected for Standard.) |
| `replicas.overrides[].sessionTimeout` | int | No |   | Session timeout in seconds. |

### `replicaDefaults` (AgReplicaDefaultsSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `replicaDefaults.availabilityMode` | string | No | `SynchronousCommit` | Default availability mode. `SynchronousCommit` / `AsynchronousCommit`. |
| `replicaDefaults.readableSecondary` | string | No | `All` | Default readable-secondary mode. `No` / `ReadOnly` / `All` (Standard is forced to `No` by the controller). |
| `replicaDefaults.seedingMode` | string | No | `Automatic` | Replica seeding mode. |
| `replicaDefaults.backupPriority` | int | No | `50` | Default backup priority. |
| `replicaDefaults.sessionTimeout` | int | No | `10` | Default session timeout (seconds). |

### `instanceTemplate` (AgInstanceTemplateSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `instanceTemplate.version` | string | No | `2022` | SQL Server major version. |
| `instanceTemplate.image` | string | No |   | Container image (overrides the default). |
| `instanceTemplate.imageTag` | string | No |   | Image tag. |
| `instanceTemplate.memory` | object (`MemorySpec`) | No |   | Unified memory config (drives pod resources and SQL max/min server memory). |
| `instanceTemplate.memory.total` | string | No | `""` | Total pod memory (e.g. `16Gi`). Sets requests and limits (Guaranteed QoS). |
| `instanceTemplate.memory.systemReserve` | string | No | `2Gi` | Memory reserved for OS/Agent/sidecars. SQL max = total minus reserve. |
| `instanceTemplate.memory.minMemory` | string | No |   | Optional `min server memory`. |
| `instanceTemplate.resources` | object (`ResourceRequirements`) | No |   | Pod resource requests/limits. |
| `instanceTemplate.resources.requests.cpu` / `.memory` | string | No |   | CPU / memory requests. |
| `instanceTemplate.resources.limits.cpu` / `.memory` | string | No |   | CPU / memory limits. |
| `instanceTemplate.resources.cpuPinning` | bool | No | `false` | Enable CPU pinning. |
| `instanceTemplate.storage` | object (`StorageSpec`) | No |   | Persistent volume claims per volume role. |
| `instanceTemplate.storage.data` / `sysDb` / `log` / `tempDb` / `backup` / `errorLogs` / `certBackups` | object (`StorageVolumeSpec`) | No |   | Per-role volume. |
| `instanceTemplate.storage.<role>.size` | string | No | `10Gi` | Volume size. |
| `instanceTemplate.storage.<role>.storageClassName` | string | No |   | StorageClass name. |
| `instanceTemplate.storage.<role>.accessModes` | string[] | No |   | PVC access modes. |
| `instanceTemplate.configurationRef` | object (`ResourceReference`) | No |   | Reference (by `name`) to a `SqlConfiguration`. |
| `instanceTemplate.nodeSelector` | map[string]string | No |   | Pod node selector. |
| `instanceTemplate.tolerations` | array (`V1Toleration`) | No |   | Pod tolerations. |
| `instanceTemplate.priorityClassName` | string | No |   | Pod PriorityClass. |
| `instanceTemplate.runtimeClassName` | string | No |   | Pod RuntimeClass (e.g. Kata). |
| `instanceTemplate.podSecurityContext` | object (`V1PodSecurityContext`) | No |   | Pod-level security context. |
| `instanceTemplate.securityContext` | object (`V1SecurityContext`) | No |   | Container-level security context. |

### `hadrCertificate` (AgHadrCertificateSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `hadrCertificate.source` | string | No | `Generate` | `Generate` or `Import`. Only `Generate` is implemented. Other values are rejected by the webhook. |
| `hadrCertificate.model` | string | No | `PerReplica` | Certificate model. Only `PerReplica` is implemented (each replica gets its own cert, public keys cross-imported). Other values are rejected. |
| `hadrCertificate.generate` | object | No |   | Generation parameters (used when `source: Generate`). |
| `hadrCertificate.generate.subject` | string | No | `""` | Certificate subject. |
| `hadrCertificate.generate.expiryDate` | string | No |   | Certificate expiry date. |
| `hadrCertificate.generate.keyLength` | int | No | `2048` | Key length in bits. |
| `hadrCertificate.importFrom` | object | No |   | Import parameters (used when `source: Import`, not yet implemented). |
| `hadrCertificate.importFrom.secretName` | string | No | `""` | Source Secret name. |
| `hadrCertificate.importFrom.certificateKey` | string | No | `certificate.cer` | Secret key holding the certificate. |
| `hadrCertificate.importFrom.privateKeyKey` | string | No | `private-key.pvk` | Secret key holding the private key. |
| `hadrCertificate.importFrom.passwordKey` | string | No |   | Secret key holding the private-key password. |
| `hadrCertificate.secretName` | string | No |   | K8s Secret where the operator stores/reads the certificate. |
| `hadrCertificate.encryptionPasswordSecretRef` | object (`SecretReference`) | No |   | Secret with the password used to encrypt the private key on export. |

### `endpoint` (AgEndpointSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `endpoint.name` | string | No | `Hadr_endpoint` | Mirroring endpoint name. |
| `endpoint.port` | int | No | `5022` | Mirroring port. Must be `1024` to `65535` and differ from `listener.port`. |
| `endpoint.encryptionAlgorithm` | string | No | `AES` | Endpoint encryption algorithm. |

### `listener` / `readOnlyListener` (AgListenerSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `<listener>.port` | int | No | `1433` | Listener port. When `listener.port` is set it must be `1024` to `65535` and differ from `endpoint.port`. |
| `<listener>.service` | object (`ServiceSpec`) | No |   | Backing Service configuration. |
| `<listener>.service.type` | string | No | `ClusterIP` | Service type (`ClusterIP`, `NodePort`, `LoadBalancer`, …). |
| `<listener>.service.port` | int | No | `1433` | Service port. |
| `<listener>.service.annotations` | map[string]string | No |   | Service annotations. |

### `readOnlyRouting` (AgReadOnlyRoutingSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `readOnlyRouting.enabled` | bool | No | `false` | Enable read-only routing. Enterprise only. |

### `failover` (AgFailoverSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `failover.operatorManaged` | bool | No | `true` | Whether the operator manages failovers (SQL Server itself always stays `MANUAL`). |
| `failover.healthCheckIntervalSeconds` | int | No | `10` | Health-check polling interval. |
| `failover.healthCheckTimeoutSeconds` | int | No | `30` | Health-check timeout. Must be `>= 5`. |
| `failover.failoverConditionLevel` | int | No | `3` | Severity level triggering failover, `1` to `5` (1=ServerDown … 5=AnyFailure). |
| `failover.splitBrainProtection` | object | No | (see below) | Split-brain / fencing protection. |
| `failover.splitBrainProtection.enabled` | bool | No | `true` | Enable split-brain protection. |
| `failover.splitBrainProtection.fencingStrategy` | string | No | `NetworkIsolation` | `NetworkIsolation`, `ManualApproval`, or `External`. |
| `failover.splitBrainProtection.fencingTimeoutSeconds` | int | No | `60` | Fencing timeout. |
| `failover.preferredTargets[]` | array | No |   | Preferred failover targets. |
| `failover.preferredTargets[].ordinal` | int | Yes (in item) |   | Target replica ordinal. |
| `failover.allowAutomaticDataLossFailover` | bool | No | `false` | Allow operator to perform force-failover-allow-data-loss automatically. |
| `failover.autoResumeDataMovement` | bool | No | `true` | Automatically resume suspended databases after failover. |
| `failover.autoReconfigurePrimary` | bool | No | `true` | Automatically update pod labels after failover. |

### `updateStrategy` (UpdateStrategySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `updateStrategy.type` | string | No | `RollingUpdate` | Update strategy type. |
| `updateStrategy.rollingUpdate` | object | No | (see below) | Rolling update parameters. |
| `updateStrategy.rollingUpdate.maxUnavailable` | int | No | `1` | Max replicas unavailable during the rollout. |
| `updateStrategy.rollingUpdate.preUpdateBackup` | bool | No | `true` | Take a backup before updating a replica. |
| `updateStrategy.rollingUpdate.healthCheckTimeoutSeconds` | int | No | `300` | Per-pod health wait timeout. |
| `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` | int | No | `60` | Pause between pods (`nextActionAfter` gate). |
| `updateStrategy.rollingUpdate.updateSecondariesFirst` | bool | No | `true` | Update secondaries before the primary. |
| `updateStrategy.rollingUpdate.failoverBeforePrimaryUpdate` | bool | No | `true` | Fail over off the primary before updating it. |

> Note: the `RollingUpdateSpec` properties above are the canonical fields. (`delayBetweenInstances` shown in some older examples is not part of the spec.)

### `antiAffinity` (AntiAffinitySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `antiAffinity.requiredAntiAffinityWith[]` | array | No |   | Required anti-affinity against named instances. Items: `instanceName`, `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredAntiAffinityWith[]` | array | No |   | Preferred anti-affinity. Items add `weight` (default `100`). |
| `antiAffinity.requiredSelfAntiAffinity` | object | No |   | Require replicas of this AG to spread, via `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredSelfAntiAffinity` | object | No |   | Prefer spread, via `topologyKey` + `weight` (default `100`). |

When `antiAffinity` is omitted, replicas default to a *preferred* hostname spread. This prevents a single node failure from taking the quorum, while still allowing single-node clusters to schedule.

### `networkPolicy` (NetworkPolicySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `networkPolicy.enabled` | bool | No | `false` | Emit a CiliumNetworkPolicy for SQL + mirroring ports. |
| `networkPolicy.allowedNamespaces` | string[] | No |   | Namespaces permitted to reach the AG. |
| `networkPolicy.operatorNamespace` | string | No |   | Operator namespace allowed for control traffic. |

## Status

| Field | Type | Description |
|---|---|---|
| `phase` | string | High-level phase (default `Pending`). |
| `observedGeneration` | long | Last reconciled `metadata.generation`. |
| `edition` | string | Resolved SQL Server edition. |
| `clusterType` | string | AG cluster type (always `None`). |
| `primaryReplica` | string | Name of the current primary replica. |
| `primaryEndpoint` | string | R/W endpoint (listener) address. |
| `readOnlyEndpoint` | string | R/O endpoint address. |
| `memory` | object (`MemoryStatus`) | Memory alignment: `podLimitMb`, `sqlMaxMemoryMb`, `sqlMinMemoryMb`, `systemReserveMb`, `aligned`. |
| `replicas[]` | array (`AgReplicaStatus`) | Per-replica status (see below). |
| `databaseSummary` | object (`AgDatabaseSummaryStatus`) | Aggregated roll-up of `SqlAvailabilityGroupDatabase` CRDs for this AG. |
| `databaseSummary.total` / `synchronized` / `synchronizing` / `suspended` / `failed` | int | O(1) database counts by state. The former per-database `databases[]` list (unbounded by database count) has been removed; per-database phase is served via the individual `SqlAvailabilityGroupDatabase` resources, and live per-replica health is served via the `view.mssql.operator` API (`kubectl get agreplicastates`). |
| `certificates[]` | array (`AgCertificateStatus`) | Per-cert: `replicaName`, `name`, `thumbprint`, `expiryDate`, `trustedByReplicas[]`. |
| `lastFailover` | object (`AgLastFailoverStatus`) | Last failover: `timestamp`, `from`, `to`, `reason`, `dataLoss`. |
| `rollingUpdate` | object (`AgRollingUpdateStatus`) | In-flight rolling update progress (see below). |
| `readOnlyRoutingSignature` | string | Signature of the read-only routing topology last applied to SQL Server. Routing is re-applied only when it changes (failover, replica add/remove, readability flip). |
| `conditions[]` | array (`ConditionStatus`) | Standard conditions: `type`, `status`, `reason`, `message`, `lastTransitionTime`. |

`kubectl get sqlavailabilitygroup` shows: `PHASE` (`.status.phase`), `PRIMARY` (`.status.primaryReplica`), and `AGE`.

> Under the etcd-light status model the AG keeps `replicas[]` and `certificates[]` inline because they are bounded by replica count (≤ 9). Only the unbounded per-database list under `databaseSummary` was removed; that detail now lives in the `view.mssql.operator` aggregated read API and the `SqlAvailabilityGroupDatabase` resources.

### `status.replicas[]` (AgReplicaStatus)

| Field | Type | Description |
|---|---|---|
| `name` | string | Replica (pod) name. |
| `role` | string | `Primary` / `Secondary` / `Unknown`. |
| `availabilityMode` | string | `SynchronousCommit` / `AsynchronousCommit` / `Unknown`. |
| `synchronizationState` | string | `Synchronized` / `Synchronizing` / `Unknown`, etc. |
| `connectionState` | string | Replica connection state. |
| `isFailoverReady` | bool | True if the replica is a safe failover target. |
| `readable` | bool | Whether the replica currently serves read-only connections. |
| `listenerReady` | string | Listener readiness for this replica (`none` by default). |
| `lastHeartbeat` | DateTime? | Last observed heartbeat. |
| `logSendQueueSize` | long | Total log send queue (KB), a replication lag indicator. |
| `redoQueueSize` | long | Total redo queue (KB), a read-staleness indicator. |

### `status.rollingUpdate` (AgRollingUpdateStatus)

| Field | Type | Description |
|---|---|---|
| `phase` | string | `Idle` / `Planning` / `UpdatingSecondary` / `FailingOverForUpdate` / `UpdatingPrimary` / `Completing` / `Failed`. |
| `currentPod` | string | Pod currently being updated. |
| `step` | string | Fine-grained step: `DeletingPod` / `WaitingHealthy` / `Pausing`. |
| `totalPods` | int | Total pods in the rollout. |
| `updatedPods` | int | Pods already updated. |
| `startedAt` | DateTime? | Rollout start time. |
| `currentStepStartedAt` | DateTime? | Current step start time. |
| `nextActionAfter` | DateTime? | Earliest time the next pod may start (`pauseBetweenPodsSeconds` gate). |
| `targetRevision` | string | StatefulSet `updateRevision` the rollout converges toward. |

## More examples

A fuller AG showing listeners, read-only routing, anti-affinity and an AG-aware rolling update strategy:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mssql-sa-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "YourStr0ngP@ssw0rd!"
---
apiVersion: v1
kind: Secret
metadata:
  name: mssql-master-key-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "An0therStr0ngP@ss!"
---
apiVersion: mssql.operator/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: my-ag
  namespace: mssql-ag-demo
spec:
  edition: Enterprise
  replicas:
    count: 3
    synchronousCount: 2
    requiredSynchronizedSecondariesToCommit: 1
  saPasswordSecretRef:
    name: mssql-sa-password
    key: password
  masterKeyPasswordSecretRef:
    name: mssql-master-key-password
    key: password
  hadrCertificate:
    source: Generate
    model: PerReplica
    generate:
      subject: "CN=my-ag-hadr"
      keyLength: 2048
  instanceTemplate:
    version: "2022"
    storage:
      data:
        size: 20Gi
        storageClassName: fast-ssd
      log:
        size: 10Gi
        storageClassName: fast-ssd
    resources:
      requests:
        cpu: "2"
        memory: "8Gi"
      limits:
        cpu: "4"
        memory: "16Gi"
  listener:
    port: 1433
    service:
      type: LoadBalancer
  readOnlyListener:
    port: 1434
    service:
      type: ClusterIP
  readOnlyRouting:
    enabled: true
  antiAffinity:
    preferredSelfAntiAffinity:
      topologyKey: kubernetes.io/hostname
  updateStrategy:
    type: RollingUpdate
    rollingUpdate:
      maxUnavailable: 1
      pauseBetweenPodsSeconds: 120
      updateSecondariesFirst: true
      failoverBeforePrimaryUpdate: true
```

## Validation & behaviour

### Webhook validation (`SqlAvailabilityGroupValidator`)

Create and update both run the same rules:

- `edition` must be `Standard`, `Enterprise`, or `Developer`.
- `saPasswordSecretRef`, `masterKeyPasswordSecretRef`, and `hadrCertificate` are **required**.
- `hadrCertificate.source` must be `Generate` and `hadrCertificate.model` must be `PerReplica`. These are the only combinations the controller implements. Other values are rejected, not silently coerced.
- `endpoint.port` must be `1024` to `65535`. When `listener` is set, `listener.port` must be `1024` to `65535` and must differ from `endpoint.port`.
- `failover.healthCheckTimeoutSeconds` must be `>= 5`. `failover.failoverConditionLevel` must be `1` to `5`.
- `replicas.requiredSynchronizedSecondariesToCommit` must be `< replicas.synchronousCount`. `synchronousCount` cannot exceed `count`.
- `instanceTemplate.memory`, when present, is validated by `MemorySpecValidator`.
- **Standard Edition:** `count` must be exactly `2`. `readOnlyListener`, `readOnlyRouting.enabled: true`, and `backupPreference` are rejected. Replica overrides may not request `AsynchronousCommit`, a readable secondary other than `No`, or a `backupPriority`.
- **Enterprise/Developer:** `count` must be `2` to `9`.

Database-level constraints are enforced by the separate `SqlAvailabilityGroupDatabase` validator, not here.

### Reconcile behaviour

- **AG bootstrap:** the operator deploys the replica StatefulSet, exchanges per-replica HADR certificates (`PerReplica` model, public keys cross-imported), creates the mirroring endpoints, and forms the AG with `CLUSTER_TYPE = NONE` and `FAILOVER_MODE = MANUAL`.
- **Role & readability labels:** pod labels reflect each replica's role (primary/secondary) and readability so the R/W and R/O Services select the correct backends.
- **Listener routing:** the R/W listener targets the current primary. The R/O listener (Enterprise) targets readable secondaries. With `readOnlyRouting.enabled`, SQL Server read-only routing is configured and re-applied only when `status.readOnlyRoutingSignature` changes (failover, replica add/remove, readability flip).
- **REQUIRED_SYNC adjustment:** the operator maintains `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` consistent with the synchronous topology, keeping it below the synchronous replica count to preserve availability.
- **Operator-orchestrated failover:** because SQL stays `MANUAL`, the operator watches replica health (`failover.*`) and performs failover, optionally fencing the old primary (`splitBrainProtection`), resuming data movement (`autoResumeDataMovement`) and relabeling (`autoReconfigurePrimary`). Force-failover-with-data-loss is only attempted when `allowAutomaticDataLossFailover` is set.
- **AG-aware rolling updates:** updates roll secondaries first, optionally back up before each pod, fail the primary off before updating it, and gate each pod on health and `pauseBetweenPodsSeconds`. Progress is tracked in `status.rollingUpdate`.

## Related

- [`SqlAvailabilityGroupDatabase`](./sqlavailabilitygroupdatabase.md): databases joined to this AG.
- [`SqlTdeCertificate`](./sqltdecertificate.md): TDE certificates for encrypted databases.
- [`SqlConfiguration`](./sqlconfiguration.md): referenced via `instanceTemplate.configurationRef`.
- [`SqlInstance`](./sqlinstance.md): standalone (non-AG) SQL Server instances.
