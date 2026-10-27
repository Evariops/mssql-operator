# `SqlAvailabilityGroup`

> Declaratively provisions a SQL Server Always On Availability Group across a set of replicas, with operator-orchestrated failover, listeners and read routing.

`apiVersion: mssql-operator.evariops.com/v1alpha1` · `kind: SqlAvailabilityGroup` · scope: Namespaced · plural: `sqlavailabilitygroups`

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
apiVersion: mssql-operator.evariops.com/v1alpha1
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
| `operatorLoginSecretRef` | object (`SqlLoginSecretReference`) | No |   | Reserved: a dedicated operator login is not implemented (the operator uses `sa`). Refused at admission (not implemented). |
| `listener` | object | No |   | Read/Write listener service. |
| `readOnlyListener` | object | No |   | Read-Only listener service. Enterprise only, rejected for Standard. |
| `readOnlyRouting` | object | No |   | Read-only routing configuration. Enterprise only, cannot be enabled on Standard. |
| `failover` | object | No | (see below) | Failover behaviour and split-brain protection. |
| `updateStrategy` | object | No |   | AG-aware rolling update strategy. |
| `antiAffinity` | object | No | (see `topologySpread`) | Pod anti-affinity rules. |
| `topologySpread` | object | No | preferred node+zone self-spread | Topology spread & cross-instance placement. See [`topologySpread`](#topologyspread-topologyspreadspec). |
| `pool` | string | No |   | Cross-resource grouping label (`mssql-operator.evariops.com/pool`) for pool-scoped spread / anti-affinity. |
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
| `instanceTemplate.cumulativeUpdate` | string | No | (operator default) | The exact cumulative update **every replica** runs: `RTM`, `CU25`, `CU32-GDR1`. One build for the whole group is the point — replicas on different builds is the state an AG cannot sustain. Changing it is what a rolling CU upgrade rolls. Mutually exclusive with `imageTag`; a CU downgrade is rejected at admission (see [SqlInstance › pinning the build](sqlinstance.md#pinning-the-build)). |
| `instanceTemplate.baseOs` | string | No | (operator default) | The OS base every replica's image is built on (`ubuntu-22.04`, `ubuntu-24.04`), restricted to what the declared `version` ships under. Re-basing is a rolling pod restart, not a version move. Mutually exclusive with `imageTag`. |
| `instanceTemplate.image` | string | No | `mcr.microsoft.com/mssql/server` | Container image repository, full reference, or `@sha256:` digest (overrides the default). |
| `instanceTemplate.imageTag` | string | No | (operator default) | Pin a tag the operator does not know how to build itself. Floating tags (`2022-latest`) are rejected. |
| `instanceTemplate.memory` | object (`MemorySpec`) | No |   | Unified memory config (drives pod resources and SQL max/min server memory). |
| `instanceTemplate.memory.total` | string | No | `""` | Total pod memory (e.g. `16Gi`). Sets requests and limits (Guaranteed QoS). |
| `instanceTemplate.memory.systemReserve` | string | No | `2Gi` | Memory reserved for OS/Agent/sidecars. SQL max = total minus reserve. |
| `instanceTemplate.memory.minMemory` | string | No |   | Optional `min server memory`. |
| `instanceTemplate.resources` | object (`ResourceRequirements`) | No |   | Pod resource requests/limits. |
| `instanceTemplate.resources.requests.cpu` / `.memory` / `.ephemeralStorage` | string | No |   | CPU / memory / ephemeral-storage requests. |
| `instanceTemplate.resources.limits.cpu` / `.memory` / `.ephemeralStorage` | string | No |   | CPU / memory / ephemeral-storage limits. |
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
| `instanceTemplate.shutdown.gracePeriodSeconds` | int (int64) | No | `180` | Pod `terminationGracePeriodSeconds` for clean checkpointing on shutdown. |
| `instanceTemplate.podSecurityContext` | object (`V1PodSecurityContext`) | No |   | Pod-level security context. |
| `instanceTemplate.securityContext` | object (`V1SecurityContext`) | No |   | Container-level security context. Privileged containers, root, capabilities beyond the baseline set (plus `IPC_LOCK`), unconfined seccomp/AppArmor and unsafe sysctls are refused. |
| `instanceTemplate.tls.secretName` | string | No |   | `kubernetes.io/tls` Secret every replica presents; SANs covering `*.<name>-headless.<namespace>.svc.cluster.local` and the listener Services. |
| `instanceTemplate.tls.forceEncryption` | bool | No | `false` | Refuse unencrypted client connections. |

### `hadrCertificate` (AgHadrCertificateSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `hadrCertificate.source` | string | No | `Generate` | `Generate` or `Import`. Only `Generate` is implemented. Other values are rejected by the webhook. |
| `hadrCertificate.model` | string | No | `PerReplica` | Certificate model. Only `PerReplica` is implemented (each replica gets its own cert, public keys cross-imported). Other values are rejected. |
| `hadrCertificate.generate` | object | No |   | Generation parameters (used when `source: Generate`). |
| `hadrCertificate.generate.subject` | string | No | `""` | Certificate subject. |
| `hadrCertificate.generate.expiryDate` | string | No |   | `EXPIRY_DATE` of the certificates created from then on (`yyyy-MM-dd`); SQL Server's default is one year. |
| `hadrCertificate.generate.keyLength` | int | No | `2048` | Only `2048`: `CREATE CERTIFICATE` has no key-length option. |
| `hadrCertificate.importFrom` | object | No |   | Import parameters (used when `source: Import`, not yet implemented). |
| `hadrCertificate.importFrom.secretName` | string | No | `""` | Source Secret name. |
| `hadrCertificate.importFrom.certificateKey` | string | No | `certificate.cer` | Secret key holding the certificate. |
| `hadrCertificate.importFrom.privateKeyKey` | string | No | `private-key.pvk` | Secret key holding the private key. |
| `hadrCertificate.importFrom.passwordKey` | string | No |   | Secret key holding the private-key password. |
| `hadrCertificate.secretName` | string | No |   | Reserved. Refused at admission (not implemented). |
| `hadrCertificate.encryptionPasswordSecretRef` | object (`SecretReference`) | No |   | Secret with the password used to encrypt the private key on export. |

### `endpoint` (AgEndpointSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `endpoint.name` | string | No | `Hadr_endpoint` | Mirroring endpoint name. |
| `endpoint.port` | int | No | `5022` | Mirroring port. Must be `1024` to `65535` and differ from `listener.port`. |
| `endpoint.encryptionAlgorithm` | string | No | `AES` | Endpoint encryption algorithm: only `AES` (RC4 is refused). |

### `listener` / `readOnlyListener` (AgListenerSpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `<listener>.port` | int | No | `1433` | Listener port. When `listener.port` is set it must be `1024` to `65535` and differ from `endpoint.port`. |
| `<listener>.service` | object (`ServiceSpec`) | No |   | Backing Service configuration. |
| `<listener>.service.type` | string | No | `ClusterIP` | Service type: `ClusterIP`, `NodePort` or `LoadBalancer`. |
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
| `failover.healthCheckTimeoutSeconds` | int | No | `15` | Deadline of one health pass over every replica (a replica that has not answered counts as unreachable). Must be `>= 5`. |
| `failover.failoverConditionLevel` | int | No | `2` | Only `2` is implemented: failover when the primary is down or does not answer (SQL Server errors do not trigger one). |
| `failover.primaryUnreachableThreshold` | int | No | `3` | Consecutive health passes with no reachable primary before the operator acts (drains the writer label, promotes a secondary). One missed pass is a monitoring blip, not an outage. |
| `failover.splitBrainProtection` | object | No | (see below) | Split-brain / fencing protection. |
| `failover.splitBrainProtection.enabled` | bool | No | `true` | Cannot be disabled; use `fencingStrategy` `ManualApproval`/`External` to keep fencing in your hands. |
| `failover.splitBrainProtection.fencingStrategy` | string | No | `NetworkIsolation` | `NetworkIsolation`, `ManualApproval`, or `External`. |
| `failover.splitBrainProtection.fencingTimeoutSeconds` | int | No | `60` | Fencing timeout. |
| `failover.preferredTargets[]` | array | No |   | Preferred failover targets. |
| `failover.preferredTargets[].ordinal` | int | Yes (in item) |   | Target replica ordinal. |
| `failover.allowAutomaticDataLossFailover` | bool | No | `false` | Allow operator to perform force-failover-allow-data-loss automatically. |
| `failover.autoResumeDataMovement` | bool | No | `true` | Always on (each `SqlAvailabilityGroupDatabase` resumes data movement); `false` is refused. |
| `failover.autoReconfigurePrimary` | bool | No | `true` | Always on (the listener selects on the role labels); `false` is refused. |

### `updateStrategy` (UpdateStrategySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `updateStrategy.type` | string | No | `RollingUpdate` | Update strategy type. |
| `updateStrategy.rollingUpdate` | object | No | (see below) | Rolling update parameters. |
| `updateStrategy.rollingUpdate.maxUnavailable` | int | No | `1` | Only `1`: replicas are updated one at a time. |
| `updateStrategy.rollingUpdate.preUpdateBackup` | bool | No | `false` | Not implemented: `true` is refused (schedule your own backup). |
| `updateStrategy.rollingUpdate.healthCheckTimeoutSeconds` | int | No | `300` | Per-pod health wait timeout. |
| `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` | int | No | `60` | Pause between pods (`nextActionAfter` gate). |
| `updateStrategy.rollingUpdate.updateSecondariesFirst` | bool | No | `true` | Always on (secondaries, then the primary); `false` is refused. |
| `updateStrategy.rollingUpdate.failoverBeforePrimaryUpdate` | bool | No | `true` | Fail over off the primary before updating it. |

> Note: the `RollingUpdateSpec` properties above are the canonical fields. (`delayBetweenInstances` shown in some older examples is not part of the spec.)

### `antiAffinity` (AntiAffinitySpec)

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `antiAffinity.requiredAntiAffinityWith[]` | array | No |   | Required anti-affinity against named instances. Items: `instanceName`, `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredAntiAffinityWith[]` | array | No |   | Preferred anti-affinity. Items add `weight` (default `100`). |
| `antiAffinity.requiredSelfAntiAffinity` | object | No |   | Require replicas of this AG to spread, via `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredSelfAntiAffinity` | object | No |   | Prefer spread, via `topologyKey` + `weight` (default `100`). |

When **both** `antiAffinity` and `topologySpread` are omitted, replicas default to a *preferred* node **and** zone self-spread (via `topologySpread`). This keeps replicas off the same node and AZ where possible — preventing a single node or zone failure from taking the quorum — while still allowing single-node / single-zone clusters to schedule. Setting either `antiAffinity` or `topologySpread` suppresses the default.

### `topologySpread` (TopologySpreadSpec)

Native `topologySpreadConstraints`. Spread *balances* replicas across topology domains (skew-bounded), unlike anti-affinity which only *separates* them — so spread expresses "1 replica per zone" cleanly. `Required` → `whenUnsatisfiable: DoNotSchedule`, `Preferred` → `ScheduleAnyway`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `topologySpread.self` | object | No |   | Spread this AG's replicas. Fields: `node`/`zone` (`Required`\|`Preferred`), `maxSkew` (default `1`), `minDomains`. Selector is the AG identity only (stable across in-place upgrades). |
| `topologySpread.pool` | object | No |   | Spread the pods of all resources sharing `spec.pool`. Same fields. Requires `spec.pool`. |
| `topologySpread.poolAntiAffinity` | string | No |   | Strict cross-pool hostname separation (`Required`\|`Preferred`). Requires `spec.pool`. |
| `topologySpread.constraints` | []TopologySpreadConstraint | No |   | Raw native constraints appended verbatim (escape hatch). |

> **Multi-AZ footgun.** `zone: Required` (`maxSkew: 1`) with more replicas than available zones leaves the surplus replica `Pending` (e.g. 3 replicas / 2 zones). The webhook cannot detect this (it does not know the cluster topology); use `Preferred` if unsure.

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
| `image` | string | The image the operator resolved for every replica — the build the group is *meant* to run. |
| `cumulativeUpdate` | string | The patch level that image deploys, e.g. `CU25`. Compare it with `primaryProductVersion` to see whether a rolling CU upgrade has actually landed. |
| `baseOs` | string | The OS base that image is built on, e.g. `ubuntu-22.04`. |
| `primaryProductVersion` | string | `SERVERPROPERTY('ProductVersion')` of the last confirmed primary, e.g. `16.0.4195.2`. A replica running an older major version is never promoted: it cannot redo the newer log. |
| `clusterType` | string | AG cluster type (always `None`). |
| `primaryReplica` | string | Name of the current primary replica. |
| `primaryUnreachableCount` | int | Consecutive health passes where no primary answered. Reset on the first healthy pass; failover only triggers once it reaches `failover.primaryUnreachableThreshold`. |
| `primaryEndpoint` | string | R/W endpoint (listener) address. |
| `readOnlyEndpoint` | string | R/O endpoint address. |
| `memory` | object (`MemoryStatus`) | Memory alignment: `podLimitMb`, `sqlMaxMemoryMb`, `sqlMinMemoryMb`, `systemReserveMb`, `aligned`. |
| `replicas[]` | array (`AgReplicaStatus`) | Per-replica status (see below). |
| `databaseSummary` | object (`AgDatabaseSummaryStatus`) | Aggregated roll-up of `SqlAvailabilityGroupDatabase` CRDs for this AG. |
| `databaseSummary.total` / `synchronized` / `synchronizing` / `suspended` / `failed` | int | O(1) database counts by state. The former per-database `databases[]` list (unbounded by database count) has been removed; per-database phase is served via the individual `SqlAvailabilityGroupDatabase` resources, and live per-replica health is served via the `view.mssql-operator.evariops.com` API (`kubectl get agreplicastates`). |
| `certificates[]` | array (`AgCertificateStatus`) | Per-cert: `replicaName`, `name`, `thumbprint`, `expiryDate`, `trustedByReplicas[]`. |
| `lastFailover` | object (`AgLastFailoverStatus`) | Last failover: `timestamp`, `from`, `to`, `reason`, `dataLoss`, `unsentLogKb` (log the promoted replica was missing), `forkLsns` (data-loss promotions only: hardened LSN of each database on the promoted replica — a secondary holding log beyond it is divergent and rejoins only with the `approve-data-loss-resume` annotation). |
| `rollingUpdate` | object (`AgRollingUpdateStatus`) | In-flight rolling update progress (see below). |
| `readOnlyRoutingSignature` | string | Signature of the read-only routing topology last applied to SQL Server. Routing is re-applied only when it changes (failover, replica add/remove, readability flip). |
| `conditions[]` | array (`ConditionStatus`) | Standard conditions: `type`, `status`, `reason`, `message`, `lastTransitionTime`. `NeedsAttention` is True only while a human decision is required — `SplitBrain` (writes drained, no automatic arbiter), `NoLosslessFailoverTarget` / `DataLossFailoverBlocked` (no primary and no automatic way out: the message says whether the replica pods are down or the operator itself is cut off), `RollingUpdateFailed` (annotate `retry-rolling-update=true` once the cause is fixed). It is exported as `mssqloperator_needs_attention`, the one gauge to alert on. |

`kubectl get sqlavailabilitygroup` shows: `PHASE` (`.status.phase`), `PRIMARY` (`.status.primaryReplica`), `CU` (`.status.cumulativeUpdate`), `BUILD` (`.status.primaryProductVersion`), and `AGE`.

> Under the etcd-light status model the AG keeps `replicas[]` and `certificates[]` inline because they are bounded by replica count (≤ 9). Only the unbounded per-database list under `databaseSummary` was removed; that detail now lives in the `view.mssql-operator.evariops.com` aggregated read API and the `SqlAvailabilityGroupDatabase` resources.

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

> Live telemetry (heartbeat, log-send-queue, redo-queue) is deliberately **not** in the status: those values changed on every reconcile, so writing them back triggered a watch event that triggered another reconcile — a permanent etcd hot-loop. Replication freshness and lag now live in the `view.mssql-operator.evariops.com` API (`kubectl get agreplicastates`) and the Prometheus gauges; the status only keeps convergence state.

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
apiVersion: mssql-operator.evariops.com/v1alpha1
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
- `failover.healthCheckTimeoutSeconds` must be `>= 5`. `failover.failoverConditionLevel` must be `2`.
- Values accepted by the schema but not implemented are refused (on an update that keeps a value the stored object already carried, admission only warns): see the field table.
- Volumes under `instanceTemplate.storage` cannot be added or removed, their `storageClassName` and `accessModes` are immutable, and they can only grow (the PVCs are expanded and the StatefulSet recreated around its pods).
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
- **Operator-orchestrated failover:** because SQL stays `MANUAL`, the operator watches replica health (`failover.*`) and performs failover, fencing the old primary (`splitBrainProtection`), resuming data movement and relabeling the pods. Force-failover-with-data-loss is only attempted when `allowAutomaticDataLossFailover` is set.
- **AG-aware rolling updates:** updates roll secondaries first (one at a time), fail the primary off before updating it, and gate each pod on health and `pauseBetweenPodsSeconds`. Progress is tracked in `status.rollingUpdate`.
- **PodDisruptionBudget:** the operator always maintains a PDB with `maxUnavailable: 1` over the replica StatefulSet, so a voluntary node drain can never evict more than one AG replica at a time. This is unconditional (no spec field) — unlike the opt-in `podDisruptionBudget` on a standalone `SqlInstance`.

## Related

- [`SqlAvailabilityGroupDatabase`](./sqlavailabilitygroupdatabase.md): databases joined to this AG.
- [`SqlTdeCertificate`](./sqltdecertificate.md): TDE certificates for encrypted databases.
- [`SqlConfiguration`](./sqlconfiguration.md): referenced via `instanceTemplate.configurationRef`.
- [`SqlInstance`](./sqlinstance.md): standalone (non-AG) SQL Server instances.
