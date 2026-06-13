# Aggregated read-only view API (`view.mssql-operator.evariops.com`)

Under the etcd-light status model, CRD `status` holds only a convergence signal (`phase`,
`conditions`) and O(1) roll-up scalars. The per-item detail that used to live in unbounded status trees
— per-database encryption state, per-procedure deployment, per-target login/role membership, live SQL
Agent run history, per-replica AG health — is **never stored in etcd**. Instead it is served live, on
demand, by a Kubernetes **aggregated API server** hosted in-process by the operator.

This means you query it with plain `kubectl get`, with normal RBAC, while every answer is computed fresh
from SQL Server at request time. Nothing is persisted, so there is no write amplification, no
self-reconcile feedback loop, and no per-object size limit to hit.

## Resources

All resources are namespaced and read-only (`get`, `list` — no `watch`, no writes). The group is
`view.mssql-operator.evariops.com`, version `v1alpha1`.

| Resource | Short | Replaces (removed from status) | Computed from |
|---|---|---|---|
| `tdedatabasestates` | `tdedb` | `SqlTdeCertificate` `databaseSelection.databases[]` | `sys.dm_database_encryption_keys` per selected database |
| `storedproceduredeployments` | `spdeploy` | `SqlStoredProcedure` `deployedProcedures[]` | `sys.procedures` per matched database |
| `sqlusertargets` | `sut` | `SqlUser` `targetStatuses[]` (`databaseUsers[]`, `roles[]`) | `sys.server_principals` / `sys.database_principals` + role-membership catalogs |
| `sqlagentjobruns` | `jobrun` | `SqlAgentJob` `targetStatuses[]` (`lastRunDate`, …) | `msdb.dbo.sysjobs` / `sysjobhistory` / `sysjobactivity` |
| `agreplicastates` | `agrep` | `SqlAvailabilityGroup` per-replica detail | `sys.dm_hadr_availability_replica_states` on the primary |

### Examples

```bash
kubectl get tdedatabasestates
# NAME                   CERTIFICATE   TARGET   DATABASE   ENCRYPTED   STATE
# tde-cert.inst.appdb    tde-cert      inst     appdb      true        Encrypted

kubectl get storedproceduredeployments
kubectl get sqlusertargets
kubectl get sqlagentjobruns
kubectl get agreplicastates
```

Every resource is computed across all targets the owning CR selects. For **Availability Group**
targets the provider connects to the live **primary replica** (where the queried catalog/DMV truth
lives), exactly as the controllers do. Unreachable targets are skipped, never failing the whole list.

`kubectl get … -o wide` shows lower-priority columns (e.g. `agreplicastates` adds `AVAILABILITYMODE`
and `ALLOWCONNECTIONS`). `-o json`/`-o yaml` returns the full item.

## How requests are authenticated and authorized

The API is registered as an `APIService` so the kube-aggregator front-proxies `kubectl` requests to the
operator over mTLS. Two gates run on every request (see
[`AggregatorAuthorizer`](../../src/MsSqlOperator/Operator/ViewApi/AggregatorAuthorizer.cs) and
[`FrontProxyAuthenticator`](../../src/MsSqlOperator/Operator/ViewApi/FrontProxyAuthenticator.cs)):

1. **Authentication (front-proxy mTLS).** The operator validates the aggregator's client certificate
   against `requestheader-client-ca-file` and the allowed CNs from `requestheader-allowed-names`, both
   read from the `extension-apiserver-authentication` ConfigMap in `kube-system`. Only then does it
   trust the forwarded `X-Remote-User` / `X-Remote-Group` identity headers. Kestrel negotiates the
   client cert with `ClientCertificateMode=AllowCertificate` (admission-webhook calls, which present no
   client cert, are unaffected). The CA/allowed-names config is cached and refreshed on a TTL, so a
   rotated front-proxy CA is picked up without restarting the operator.
2. **Authorization (RBAC).** A `SubjectAccessReview` delegates the access decision to the API server, so
   normal RBAC on the `view.mssql-operator.evariops.com` resources applies to the authenticated identity.

### Granting access

The chart ships a `…-view-reader` ClusterRole with `get`/`list` on all five resources. Bind it to your
viewers:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: mssql-view-readers
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: release-name-mssql-operator-view-reader
subjects:
  - kind: Group
    name: my-sql-admins
    apiGroup: rbac.authorization.k8s.io
```

## Enabling / disabling

Controlled by `viewApi.enabled` (default `true`) in the Helm chart. It reuses the webhook serving
certificate/CA, so it requires `webhook.enabled = true`. When enabled the chart creates the
`APIService`, the auth-delegator ClusterRole/Binding (for SubjectAccessReviews/TokenReviews), the
`extension-apiserver-authentication-reader` RoleBinding in `kube-system`, and the `…-view-reader`
ClusterRole, and sets `Kestrel__Endpoints__Https__ClientCertificateMode=AllowCertificate`.

## Notes

- **Discovery.** The operator serves the group/version discovery documents and minimal-but-valid
  `/openapi/v2` and `/openapi/v3` documents so the aggregator's discovery probes succeed without
  warnings. `kubectl get` itself prints via server-side `Table` responses, so no per-field OpenAPI
  schema is required.
- **No watch.** These resources are computed on demand and are not watchable; `ListMeta.resourceVersion`
  is a constant placeholder. Use them for inspection (`kubectl get`), not for controllers/informers.
