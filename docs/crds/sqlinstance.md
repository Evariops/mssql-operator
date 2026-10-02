# `SqlInstance`

> Declarative definition of a single SQL Server instance running as a Kubernetes StatefulSet, managed end-to-end by the operator.

`apiVersion: mssql-operator.evariops.com/v1alpha1` · `kind: SqlInstance` · scope: Namespaced · plural: `sqlinstances`

## Minimal example

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mssql-sa-password
  namespace: mssql-demo
type: Opaque
stringData:
  password: "YourStr0ngP@ssw0rd!"
---
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlInstance
metadata:
  name: my-sql-instance
  namespace: mssql-demo
spec:
  saPasswordSecretRef:
    name: mssql-sa-password
    key: password
  storage:
    data:
      size: 10Gi
```

## Overview

A `SqlInstance` provisions one SQL Server instance. The operator reconciles it into a StatefulSet (with its PersistentVolumeClaims), a Service that exposes the instance, the SA password wiring, and optionally a network policy and Service Master Key backup. On every reconcile it rebuilds the desired StatefulSet, applies it, ensures the Service and supporting secrets, aligns SQL Server memory when a `memory` block is set, and writes status back to the resource. A config hash on the StatefulSet triggers rolling restarts when configuration changes.

Use a `SqlInstance` for a standalone server (`mode: SingleNode`) or as a member of an Always On Availability Group (`mode: AvailabilityGroupMember`). When you need a managed multi-replica AG with coordinated failover, model the topology with a [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md) instead of wiring members by hand.

## Spec

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `mode` | string (enum) | No | `SingleNode` | Deployment mode. Only `SingleNode` is implemented; use a `SqlAvailabilityGroup` for replicas. |
| `version` | string (enum) | No | `2022` | SQL Server major version. Allowed: `2019`, `2022`, `2025`. |
| `edition` | string (enum) | No | `Developer` | SQL Server edition. Allowed: `Developer`, `Express`, `Standard`, `Enterprise`. Every edition runs the same image; the edition is selected at runtime via `MSSQL_PID`. |
| `cumulativeUpdate` | string | No | (operator default) | The exact cumulative update this instance runs: `RTM`, `CU25`, or `CU32-GDR1`. Combined with `baseOs` to build the tag (e.g. `CU19` + `2022` → `mcr.microsoft.com/mssql/server:2022-CU19-ubuntu-22.04`). Mutually exclusive with `imageTag`. See [pinning the build](#pinning-the-build). |
| `baseOs` | string | No | (operator default) | The OS base the image is built on: `ubuntu-22.04`, `ubuntu-24.04`. Restricted to what the declared `version` actually ships under — 2019: `ubuntu-20.04`; 2022: `ubuntu-22.04`, `ubuntu-20.04`; 2025: `ubuntu-22.04`, `ubuntu-24.04`. Mutually exclusive with `imageTag`. |
| `image` | string | No | `mcr.microsoft.com/mssql/server` | Override the container image repository (or a full reference, including a `@sha256:` digest). |
| `imageTag` | string | No | (operator default) | Pin a tag the operator does not know how to build itself. Mutually exclusive with `cumulativeUpdate`. |
| `memory` | object | No |   | Unified memory configuration. Drives SQL Server `max/min server memory` alignment. See [memory](#memory). |
| `resources` | object | No |   | Pod CPU/memory requests and limits, plus CPU pinning. See [resources](#resources). |
| `storage` | object | No |   | PersistentVolumeClaim definitions per SQL volume. See [storage](#storage). |
| `service` | object | No |   | Service exposing the instance. See [service](#service). |
| `saPasswordSecretRef` | object | No |   | Reference to the Secret holding the SA password. See [secret references](#secret-references). |
| `antiAffinity` | object | No |   | Pod anti-affinity rules. See [antiAffinity](#antiaffinity). |
| `topologySpread` | object | No |   | Topology spread & cross-instance placement. See [topologySpread](#topologyspread). |
| `pool` | string | No |   | Cross-resource grouping label (`mssql-operator.evariops.com/pool`) for pool-scoped spread / anti-affinity. |
| `updateStrategy` | object | No |   | Rolling update behaviour. See [updateStrategy](#updatestrategy). |
| `configurationRef` | object | No |   | Reference to a `SqlConfiguration` to apply. Holds a single `name` field. |
| `networkPolicy` | object | No |   | Cilium network policy generation. See [networkPolicy](#networkpolicy). |
| `nodeSelector` | map[string]string | No |   | Node selector applied to the pod. |
| `tolerations` | []Toleration | No |   | Kubernetes pod tolerations (`key`, `operator`, `value`, `effect`, `tolerationSeconds`). |
| `priorityClassName` | string | No |   | Pod PriorityClass name. |
| `runtimeClassName` | string | No |   | Pod RuntimeClass name. Names containing `kata` are treated as Kata containers (affects memory calc and validation). |
| `shutdown` | object | No |   | Pod shutdown behaviour. See [shutdown](#shutdown). |
| `podDisruptionBudget` | object | No |   | Opt-in PodDisruptionBudget for the instance. See [podDisruptionBudget](#poddisruptionbudget). |
| `podSecurityContext` | object | No |   | Standard Kubernetes `V1PodSecurityContext` applied to the pod. |
| `securityContext` | object | No |   | Standard Kubernetes `V1SecurityContext` applied to the SQL Server container. |
| `machineKeySecretRef` | object | No |   | Reference to a Secret providing the ASP.NET-style machine key material. If omitted, the operator generates and manages it. See [secret references](#secret-references). |
| `smkBackupPasswordSecretRef` | object | No |   | Reference to a Secret holding the password used to back up the Service Master Key. If omitted, the operator generates one. See [secret references](#secret-references). |

### memory

The memory block is the single source of truth for both pod sizing and SQL Server `sp_configure` memory.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `memory.total` | string (quantity) | Yes (if block present) |   | Total memory for the pod (e.g. `16Gi`). Sets both requests and limits for Guaranteed QoS. Must be ≥ 4Gi. |
| `memory.systemReserve` | string (quantity) | No | `2Gi` | Memory reserved for OS, SQL Agent and sidecars. Must be ≥ 512Mi. `max server memory` = `total` minus `systemReserve` (minus Kata overhead). |
| `memory.minMemory` | string (quantity) | No |   | Optional `min server memory`. If set, must be less than the computed SQL max memory. |

### resources

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `resources.requests.cpu` | string (quantity) | No |   | CPU request (e.g. `500m`). |
| `resources.requests.memory` | string (quantity) | No |   | Memory request (e.g. `2Gi`). |
| `resources.requests.ephemeralStorage` | string (quantity) | No |   | Ephemeral-storage request. The pod writes to ephemeral storage (SMK temp files, error log without a dedicated volume); without a request, disk-pressure eviction is unmanaged. |
| `resources.limits.cpu` | string (quantity) | No |   | CPU limit (e.g. `2`). Required when `cpuPinning` is `true` or when using a Kata runtime. |
| `resources.limits.memory` | string (quantity) | No |   | Memory limit (e.g. `4Gi`). |
| `resources.limits.ephemeralStorage` | string (quantity) | No |   | Ephemeral-storage limit. |
| `resources.cpuPinning` | boolean | No | `false` | Pin the pod to whole CPUs. Requires an integer `limits.cpu` (no millicores) and, if set, `requests.cpu` equal to `limits.cpu`. |

### storage

Each volume is an optional `StorageVolumeSpec`. The volumes are: `data`, `sysDb`, `log`, `tempDb`, `backup`, `errorLogs`, `certBackups`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `storage.<volume>.size` | string (quantity) | No | `10Gi` | PVC size for the volume (e.g. `storage.data.size`). |
| `storage.<volume>.storageClassName` | string | No |   | StorageClass for the PVC. |
| `storage.<volume>.accessModes` | []string | No |   | PVC access modes (e.g. `ReadWriteOnce`). |

### service

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `service.type` | string | No | `ClusterIP` | Kubernetes Service type: `ClusterIP`, `NodePort` or `LoadBalancer`. |
| `service.port` | integer (int32) | No | `1433` | Service port for SQL Server connections. |
| `service.annotations` | map[string]string | No |   | Annotations applied to the generated Service. |

### secret references

`saPasswordSecretRef`, `machineKeySecretRef` and `smkBackupPasswordSecretRef` all use the same shape:

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | string | Yes |   | Name of the Secret in the instance namespace. |
| `key` | string | Yes |   | Key within the Secret holding the value. |

### antiAffinity

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `antiAffinity.requiredAntiAffinityWith` | []object | No |   | Hard anti-affinity against named instances. Each item: `instanceName` (string), `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredAntiAffinityWith` | []object | No |   | Soft anti-affinity against named instances. Each item: `instanceName`, `topologyKey` (default `kubernetes.io/hostname`), `weight` (int32, default `100`). |
| `antiAffinity.requiredSelfAntiAffinity` | object | No |   | Hard anti-affinity among the instance's own pods. Field: `topologyKey` (default `kubernetes.io/hostname`). |
| `antiAffinity.preferredSelfAntiAffinity` | object | No |   | Soft anti-affinity among the instance's own pods. Fields: `topologyKey` (default `kubernetes.io/hostname`), `weight` (int32, default `100`). |

### topologySpread

Native `topologySpreadConstraints`. Spread *balances* pods across topology domains (skew-bounded); anti-affinity *separates* them. `Required` → `whenUnsatisfiable: DoNotSchedule`, `Preferred` → `ScheduleAnyway`. For a single-node `SqlInstance`, only the `pool`-scoped rules are meaningful (one pod is trivially balanced).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `topologySpread.self` | object | No |   | Spread this instance's own pods. Fields: `node`/`zone` (`Required`\|`Preferred`), `maxSkew` (default `1`), `minDomains`. |
| `topologySpread.pool` | object | No |   | Spread the pods of all resources sharing `spec.pool`. Same fields as `self`. Requires `spec.pool`. |
| `topologySpread.poolAntiAffinity` | string | No |   | Strict cross-pool hostname separation (`Required`\|`Preferred`): no two pool members on the same node. Requires `spec.pool`. |
| `topologySpread.constraints` | []TopologySpreadConstraint | No |   | Raw native constraints appended verbatim (escape hatch for `minDomains`, `matchLabelKeys`, `nodeAffinityPolicy`, ...). |

> Spread vs anti-affinity: `topologySpread.pool` with `maxSkew: 1` *balances* pool members across nodes (e.g. 2-2-1 over 3 nodes); `topologySpread.poolAntiAffinity: Required` *forbids* co-location (at most one per node, surplus pods stay `Pending`). Pick the one matching your intent.

> The pool label (`mssql-operator.evariops.com/pool`) is stamped on the pod template (mutable), never the immutable selector — so a `spec.pool` change applies on the next pod recreation. Like anti-affinity, spread is evaluated at scheduling time only; existing pods are not rescheduled.

### updateStrategy

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `updateStrategy.type` | string | No | `RollingUpdate` | Update strategy type. |
| `updateStrategy.rollingUpdate` | object | No |   | Refused on a SqlInstance (AG orchestrator settings). |

### networkPolicy

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `networkPolicy.enabled` | boolean | No | `false` | When `true`, the operator generates a CiliumNetworkPolicy for the instance. |
| `networkPolicy.allowedNamespaces` | []string | No |   | Namespaces allowed to reach the instance. |
| `networkPolicy.operatorNamespace` | string | No |   | Namespace from which the operator is allowed to connect. |

### shutdown

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `shutdown.gracePeriodSeconds` | integer (int64) | No | `180` | Pod `terminationGracePeriodSeconds`. SQL Server needs time to checkpoint; the Kubernetes default of 30s SIGKILLs `sqlservr` mid-checkpoint and forces a long crash-recovery on restart. |

### podDisruptionBudget

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `podDisruptionBudget.enabled` | boolean | No | `false` | When `true`, the operator creates a PodDisruptionBudget with `maxUnavailable: 0` for the instance's pod. This makes node drains wait for an explicit, controlled eviction of the SQL pod rather than evicting it automatically — it *will* block drains until the pod is moved, so enable it knowingly. |

## Status

| Field | Type | Description |
|-------|------|-------------|
| `phase` | string | High-level lifecycle phase: `Pending` (initial), `Running`, or `Failed`. |
| `observedGeneration` | integer (int64) | `metadata.generation` last reconciled by the operator. |
| `version` | string | SQL Server version reflected from spec on the last reconcile. |
| `edition` | string | SQL Server edition reflected from spec on the last reconcile. |
| `image` | string | The image the operator resolved from `version`/`cumulativeUpdate`/`image`/`imageTag` — the build the instance is *meant* to run. |
| `cumulativeUpdate` | string | The patch level that image deploys, e.g. `CU25`. Empty for an image whose tag the operator cannot read (a custom reference or a digest). |
| `baseOs` | string | The OS base that image is built on, e.g. `ubuntu-22.04`. |
| `productVersion` | string | `SERVERPROPERTY('ProductVersion')` of the engine actually answering, e.g. `16.0.4195.2`. This is what is *running*: while a pod is still on the old image, it lags `image`, and that gap is the only honest signal that a CU upgrade has not landed yet. |
| `endpoint` | string | In-cluster connection endpoint, e.g. `<name>.<namespace>.svc.cluster.local:<port>`. |
| `memory` | object | Memory alignment status (see below). |
| `memory.podLimitMb` | integer (int32) | Pod `resources.limits.memory` in MB. |
| `memory.sqlMaxMemoryMb` | integer (int32) | Applied `sp_configure 'max server memory'` in MB. |
| `memory.sqlMinMemoryMb` | integer (int32) | Applied `sp_configure 'min server memory'` in MB (0 if unset). |
| `memory.systemReserveMb` | integer (int32) | System reserve in MB. |
| `memory.aligned` | boolean | `true` when the live SQL Server configuration matches the calculated value. |
| `podStatuses` | []object | Per-pod status. Each item: `name` (string), `ready` (bool), `role` (string, e.g. primary/secondary). Kept inline under the etcd-light status model because it is bounded by replica count. |
| `conditions` | []object | Standard conditions. The operator writes a `Ready` condition (`True` when phase is `Running`, otherwise `False`) with `reason`, `message`, and `lastTransitionTime`. |

`kubectl get sqlinstance` shows: `PHASE` (`.status.phase`), `VERSION` (`.status.version`), `CU` (`.status.cumulativeUpdate`), `BUILD` (`.status.productVersion`), `ENDPOINT` (`.status.endpoint`), and `AGE`.

## Pinning the build

`spec.version` names the major version; it does not decide which build runs. `cumulativeUpdate` and `baseOs` do:

```yaml
spec:
  version: "2025"
  cumulativeUpdate: CU6      # → mcr.microsoft.com/mssql/server:2025-CU6-ubuntu-24.04
  baseOs: ubuntu-24.04
```

Each knob moves only what it names: setting `baseOs` alone re-bases the default CU, setting `cumulativeUpdate` alone keeps the default OS base. Omit both and the instance follows the operator's defaults for that version — which means **upgrading the operator moves the CU of every instance that pins none**. Those defaults are human-owned and moved by a reviewed operator release, not by a bot: re-basing or advancing a fleet's engine is a decision, not a dependency bump.

The tag is **looked up, not guessed**. The operator carries a snapshot of what the registry publishes (`SqlServerPublishedTags.g.cs`), because the conventional spelling is often not the one Microsoft shipped:

- SQL Server 2022 only starts publishing Ubuntu 22.04 images at **CU10** — yet 22.04 is the default base, so `cumulativeUpdate: CU5` names a tag that has never existed.
- SQL Server 2022 CU2 is published as `2022-RTM-CU2`, not `2022-CU2`.
- SQL Server 2025 RTM exists on 24.04 only as a `-preview` tag.

So a build the snapshot *disproves* — one it has on other bases, or under another spelling — is **rejected at apply time**, with the shape that does exist. A build the snapshot simply does not know (a CU released after it) is accepted with a warning: a strict allowlist would make every new CU unusable until the operator itself is upgraded. The snapshot is kept current by a weekly CI job (`refresh-sql-images.yml`, or `dotnet run --project src/MsSqlOperator -- refresh-images` by hand) — never at runtime, so admission stays hermetic. A private `image` mirror is never checked against it — it carries whichever subset its owner mirrored.

Three further rules are enforced at admission:

- **The build may only move forward.** A CU downgrade (`CU25` → `CU19`) is rejected, whether you express it via `cumulativeUpdate` or `imageTag`: SQL Server cannot do it in place — the databases have already been upgraded by the newer build and the older binaries will not open them. For an AG it is worse, since an upgraded replica cannot rejoin an older primary. A rollback is a restore; if you have that plan, annotate the object with `mssql-operator.evariops.com/allow-version-downgrade: "true"` and the guard steps aside. Changing `baseOs` alone is *not* a version move: the same engine build on another Ubuntu is a pod restart.
- **No floating tags.** `imageTag: 2022-latest` (or an untagged custom `image`, which means `:latest`) is rejected: the kubelet re-resolves it on every pull, so the engine can change under the databases with nothing in the spec changing.
- **The image must match `version`.** `version: "2022"` with a tag that deploys 2019 is rejected — the engine, not the spec field, determines the database format.

For a private mirror or a build the operator does not know how to name, use `image` (+ `imageTag`, or a `@sha256:` digest). The operator then cannot read the build, says so as an admission warning, and the two guards above no longer apply to that instance.

## Validation & behaviour

The admission webhook validates a `SqlInstance` on create and update:

- **Version/edition combination** must be one of the supported pairs (`2019`/`2022`/`2025` × `Developer`/`Express`/`Standard`/`Enterprise`).
- **`mode`** must be `SingleNode` (`AvailabilityGroupMember` is refused: not implemented).
- **`updateStrategy.rollingUpdate`** is refused: it drives the availability-group orchestrator, a standalone pod is restarted by its StatefulSet.
- **`storage`**: optional volumes cannot be added or removed, `storageClassName`/`accessModes` are immutable, sizes can only grow.
- **`securityContext` / `podSecurityContext`**: privileged containers, root, added capabilities beyond the baseline set (plus `IPC_LOCK`), unconfined seccomp/AppArmor and unsafe sysctls are refused; `image` must come from an allowed registry (operator setting `allowedImageRegistries`).
- **Memory** (when `memory` is present): `total` must parse and be ≥ 4Gi. `systemReserve` must parse and be ≥ 512Mi. SQL max memory (`total` minus `systemReserve`) must be ≥ 2Gi. If `minMemory` is set, it must parse and be strictly less than SQL max memory.
- **CPU pinning** (when `resources.cpuPinning` is `true`): `resources.limits.cpu` must be set and an integer (no millicores such as `4000m`). If `resources.requests.cpu` is set, it must equal `resources.limits.cpu`.
- **Kata runtime**: if `runtimeClassName` indicates Kata (contains `kata`) and no `resources.limits.cpu` is set, the request is rejected. SQL Server would otherwise see all VM vCPUs (a licensing concern).
- **On update only**: edition changes are rejected (`Edition change is not supported in-place`). Version changes are only allowed as upgrades (no downgrades).

All validation failures are returned with HTTP status `422`.

Reconcile behaviour:

- Resolves the SA password from `saPasswordSecretRef`. If the secret or key is missing, status is set to `Failed` (reason `SaPasswordNotFound`).
- Ensures the machine-key Secret (generated if `machineKeySecretRef` is not provided).
- Builds and applies the StatefulSet, using a configuration hash so that configuration changes trigger a rolling restart. PVCs are derived from `storage`.
- Builds and applies the Service from `service` (defaults to `ClusterIP`/`1433`).
- When `memory` is set, computes and applies SQL Server max/min memory and records alignment under `status.memory`.
- Backs up the Service Master Key, generating an SMK backup password Secret when `smkBackupPasswordSecretRef` is not provided.
- When `networkPolicy.enabled` is `true`, generates a CiliumNetworkPolicy scoped to the configured namespaces and Service port.
- On success, sets `status.endpoint` and phase `Running` (reason `InstanceRunning`) and writes a `Ready` condition.

## Related

- [`SqlConfiguration`](./sqlconfiguration.md): server configuration applied via `configurationRef` or label targeting.
- [`SqlUser`](./sqluser.md): logins, users and permissions provisioned against an instance.
- [`SqlStoredProcedure`](./sqlstoredprocedure.md): stored procedures managed on databases of an instance.
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md): managed Always On AG built from instances (use instead of wiring `AvailabilityGroupMember` instances by hand).
- [`SqlTdeCertificate`](./sqltdecertificate.md): TDE certificates for encrypting databases on an instance.
- [`SqlAgentJob`](./sqlagentjob.md): SQL Server Agent jobs scheduled on an instance.
