# SqlInstance

A standalone SQL Server: one pod in a StatefulSet, its volumes and a Service. For high availability
use a [`SqlAvailabilityGroup`](sqlavailabilitygroup.md) instead.

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlInstance
metadata:
  name: reporting
  namespace: sql
spec:
  version: "2022"
  edition: Developer             # Developer (default), Express, Standard, Enterprise
  saPasswordSecretRef: { name: reporting-sa, key: password }
  memory: { total: 8Gi }
  storage:
    data: { size: 20Gi }
    log:  { size: 10Gi }
```

Clients connect to `reporting.sql.svc.cluster.local:1433`.

## What gets created

| Object | Name | Purpose |
|---|---|---|
| StatefulSet | `<name>` | the SQL Server pod `<name>-0` and its volumes |
| Service | `<name>` | port 1433 (or `service.port`) |
| Secrets | `<name>-…` | generated machine key and key backups (see [Backups and encryption](../guides/backups-and-encryption.md#key-material)) |

## Main fields

| Field | Default | What it does |
|---|---|---|
| `version` | `2022` | `2019`, `2022` or `2025`. |
| `edition` | `Developer` | Selected at runtime (`MSSQL_PID`); cannot change in place. |
| `cumulativeUpdate` / `baseOs` | operator default | Exact build (`CU25`) and OS base (`ubuntu-22.04`). See [Upgrading](../guides/upgrading.md#sql-server-builds). |
| `saPasswordSecretRef` | | Secret holding the `sa` password. Changing the Secret rotates the password. |
| `memory.total` | | Pod memory (requests = limits). `max server memory` = `total` − `systemReserve` (`2Gi` by default). At least 4Gi. |
| `resources` | | CPU requests/limits. `cpuPinning: true` pins whole CPUs (integer limit). |
| `storage.<volume>` | `10Gi` | `data`, `log`, `backup`, and optional `sysDb`, `tempDb`, `errorLogs`, `certBackups`. Sizes can only grow. |
| `service` | `ClusterIP`, `1433` | Service type, port and annotations. |
| `configurationRef.name` | | A [`SqlConfiguration`](sqlconfiguration.md) to apply (instance settings can also select it by label). |
| `tls.secretName` / `tls.forceEncryption` | | Server certificate presented to clients. See [Security](../guides/security.md). |
| `podAnnotations`, `nodeSelector`, `tolerations`, `priorityClassName`, `runtimeClassName` | | Pod placement and runtime. |
| `podDisruptionBudget.enabled` | `false` | A PDB with `maxUnavailable: 0`: node drains wait until you move the pod yourself. |
| `networkPolicy` | off | A CiliumNetworkPolicy: who may reach 1433, which destinations the pod may reach. |
| `automation.mode` | `Apply` | `Observe`, `Plan` or `Apply`. See [Automation](../guides/automation.md). |

Every other field: `kubectl explain sqlinstance.spec --recursive`.

The webhook refuses, among others: a version downgrade, a floating image tag (`2022-latest`), an image
from a registry outside the operator's allow-list, privileged or root containers, and an `image`
whose version does not match `version`.

## Status

```console
$ kubectl get sqlinstance -n sql
NAME        PHASE     VERSION   CU     BUILD         ENDPOINT                                 AGE
reporting   Running   2022      CU25   16.0.4195.2   reporting.sql.svc.cluster.local:1433     2d
```

- `status.productVersion` is the build actually running; while it differs from `status.cumulativeUpdate`, an upgrade has not landed yet.
- `status.memory.aligned` says whether SQL Server's memory settings match the pod.
- `status.conditions`: `Ready`, and **`NeedsAttention`** when a human decision is needed (for
  example the machine key Secret was replaced, or SQL Server refuses the `sa` password of the Secret).
