# `SqlInstance`

> Declarative definition of a single SQL Server instance running as a Kubernetes StatefulSet, managed end-to-end by the operator.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlInstance` · scope: Namespaced · plural: `sqlinstances`

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
apiVersion: mssql.operator/v1alpha1
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
| `mode` | string (enum) | No | `SingleNode` | Deployment mode. Allowed: `SingleNode`, `AvailabilityGroupMember`. |
| `version` | string (enum) | No | `2022` | SQL Server major version. Allowed: `2019`, `2022`, `2025`. |
| `edition` | string (enum) | No | `Developer` | SQL Server edition. Allowed: `Developer`, `Express`, `Standard`, `Enterprise`. |
| `image` | string | No | (derived) | Override the container image. If unset, the operator selects `mcr.microsoft.com/mssql/server:<version>-latest`. |
| `imageTag` | string | No | (derived) | Override only the image tag (combined with `image` or the derived repository). |
| `memory` | object | No |   | Unified memory configuration. Drives SQL Server `max/min server memory` alignment. See [memory](#memory). |
| `resources` | object | No |   | Pod CPU/memory requests and limits, plus CPU pinning. See [resources](#resources). |
| `storage` | object | No |   | PersistentVolumeClaim definitions per SQL volume. See [storage](#storage). |
| `service` | object | No |   | Service exposing the instance. See [service](#service). |
| `saPasswordSecretRef` | object | No |   | Reference to the Secret holding the SA password. See [secret references](#secret-references). |
| `antiAffinity` | object | No |   | Pod anti-affinity rules. See [antiAffinity](#antiaffinity). |
| `updateStrategy` | object | No |   | Rolling update behaviour. See [updateStrategy](#updatestrategy). |
| `configurationRef` | object | No |   | Reference to a `SqlConfiguration` to apply. Holds a single `name` field. |
| `networkPolicy` | object | No |   | Cilium network policy generation. See [networkPolicy](#networkpolicy). |
| `nodeSelector` | map[string]string | No |   | Node selector applied to the pod. |
| `tolerations` | []Toleration | No |   | Kubernetes pod tolerations (`key`, `operator`, `value`, `effect`, `tolerationSeconds`). |
| `priorityClassName` | string | No |   | Pod PriorityClass name. |
| `runtimeClassName` | string | No |   | Pod RuntimeClass name. Names containing `kata` are treated as Kata containers (affects memory calc and validation). |
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
| `resources.limits.cpu` | string (quantity) | No |   | CPU limit (e.g. `2`). Required when `cpuPinning` is `true` or when using a Kata runtime. |
| `resources.limits.memory` | string (quantity) | No |   | Memory limit (e.g. `4Gi`). |
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
| `service.type` | string | No | `ClusterIP` | Kubernetes Service type. |
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

### updateStrategy

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `updateStrategy.type` | string | No | `RollingUpdate` | Update strategy type. |
| `updateStrategy.rollingUpdate.maxUnavailable` | integer (int32) | No | `1` | Max pods unavailable during a rolling update. |
| `updateStrategy.rollingUpdate.preUpdateBackup` | boolean | No | `true` | Take a backup before updating. |
| `updateStrategy.rollingUpdate.healthCheckTimeoutSeconds` | integer (int32) | No | `300` | Per-pod health check timeout during update. |
| `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` | integer (int32) | No | `60` | Pause between pod updates. |
| `updateStrategy.rollingUpdate.updateSecondariesFirst` | boolean | No | `true` | Update AG secondaries before the primary. |
| `updateStrategy.rollingUpdate.failoverBeforePrimaryUpdate` | boolean | No | `true` | Fail over the AG before updating the primary. |

### networkPolicy

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `networkPolicy.enabled` | boolean | No | `false` | When `true`, the operator generates a CiliumNetworkPolicy for the instance. |
| `networkPolicy.allowedNamespaces` | []string | No |   | Namespaces allowed to reach the instance. |
| `networkPolicy.operatorNamespace` | string | No |   | Namespace from which the operator is allowed to connect. |

## Status

| Field | Type | Description |
|-------|------|-------------|
| `phase` | string | High-level lifecycle phase: `Pending` (initial), `Running`, or `Failed`. |
| `observedGeneration` | integer (int64) | `metadata.generation` last reconciled by the operator. |
| `version` | string | SQL Server version reflected from spec on the last reconcile. |
| `edition` | string | SQL Server edition reflected from spec on the last reconcile. |
| `endpoint` | string | In-cluster connection endpoint, e.g. `<name>.<namespace>.svc.cluster.local:<port>`. |
| `memory` | object | Memory alignment status (see below). |
| `memory.podLimitMb` | integer (int32) | Pod `resources.limits.memory` in MB. |
| `memory.sqlMaxMemoryMb` | integer (int32) | Applied `sp_configure 'max server memory'` in MB. |
| `memory.sqlMinMemoryMb` | integer (int32) | Applied `sp_configure 'min server memory'` in MB (0 if unset). |
| `memory.systemReserveMb` | integer (int32) | System reserve in MB. |
| `memory.aligned` | boolean | `true` when the live SQL Server configuration matches the calculated value. |
| `podStatuses` | []object | Per-pod status. Each item: `name` (string), `ready` (bool), `role` (string, e.g. primary/secondary). |
| `conditions` | []object | Standard conditions. The operator writes a `Ready` condition (`True` when phase is `Running`, otherwise `False`) with `reason`, `message`, and `lastTransitionTime`. |

## Validation & behaviour

The admission webhook validates a `SqlInstance` on create and update:

- **Version/edition combination** must be one of the supported pairs (`2019`/`2022`/`2025` × `Developer`/`Express`/`Standard`/`Enterprise`).
- **`mode`** must be `SingleNode` or `AvailabilityGroupMember`.
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
