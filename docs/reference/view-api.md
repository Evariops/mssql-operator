# View API

Live, read-only detail the operator does not store in the resources' status: it is read from
SQL Server when you ask. It is a Kubernetes aggregated API (`view.mssql-operator.evariops.com/v1alpha1`),
so plain `kubectl get` works, with normal RBAC.

| Resource | Shows | For |
|---|---|---|
| `agreplicastates` | role, synchronization health, connection, failover readiness of each replica | `SqlAvailabilityGroup` |
| `logchains` | full, differential and log backups recorded on each replica (latest 50), the operator's own flagged | `SqlAvailabilityGroupDatabase` |
| `tdedatabasestates` | encryption state of each selected database | `SqlTdeCertificate` |
| `storedproceduredeployments` | each procedure in each database: deployed or not | `SqlStoredProcedure` |
| `sqlusertargets` | the login and database users on each target: exists, enabled, roles | `SqlUser` |
| `sqlagentjobruns` | last run, last status, next run of the job on each target | `SqlAgentJob` |
| `operatorplans` | what the operator did, would do, or waits to be allowed to do, with the action ids | every resource (see [Automation](../guides/automation.md)) |

```bash
kubectl get agreplicastates -n sql
kubectl get operatorplans -n sql -o wide     # -o wide adds lower-priority columns
kubectl get logchains -n sql -o yaml         # full items
```

No `watch`: the data is computed on demand (cached 10 s). An unreachable target is skipped, it does
not fail the list.

## Access

The chart creates a ClusterRole `<fullname>-view-reader` (`mssql-operator-view-reader` for a release
named `mssql-operator`) with `get` and `list` on these resources. Bind it to whoever should read them:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: sql-view-readers
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: mssql-operator-view-reader
subjects:
  - kind: Group
    name: sql-admins
    apiGroup: rbac.authorization.k8s.io
```

Enabled by `viewApi.enabled` (default `true`); it needs `webhook.enabled`. While the operator is down,
the API is unavailable, which also delays namespace deletions in the whole cluster: run 2 operator
replicas (the default).
