# Custom Resource Reference

Every resource MsSqlOperator manages lives in the `mssql-operator.evariops.com/v1alpha1` API group and is
namespaced. Each page below documents the full `spec`, the `status` the operator writes back,
the admission-webhook validation rules, reconcile behaviour, and a worked example.

| Resource | Plural | What it manages |
|---|---|---|
| [`SqlInstance`](sqlinstance.md) | `sqlinstances` | A single SQL Server (StatefulSet, storage, service, and lifecycle). |
| [`SqlAvailabilityGroup`](sqlavailabilitygroup.md) | `sqlavailabilitygroups` | An Always On Availability Group (replicas, listeners, read routing, failover, rolling updates). |
| [`SqlAvailabilityGroupDatabase`](sqlavailabilitygroupdatabase.md) | `sqlavailabilitygroupdatabases` | Databases joined into an availability group, with seeding and resynchronisation. |
| [`SqlConfiguration`](sqlconfiguration.md) | `sqlconfigurations` | `sp_configure` settings, trace flags, and tempdb layout applied to matching instances. |
| [`SqlUser`](sqluser.md) | `sqlusers` | Logins, database users, and role membership. |
| [`SqlStoredProcedure`](sqlstoredprocedure.md) | `sqlstoredprocedures` | Stored procedures deployed inline or from a GitHub repository. |
| [`SqlAgentJob`](sqlagentjob.md) | `sqlagentjobs` | SQL Server Agent jobs (steps, schedules, and notifications). |
| [`SqlTdeCertificate`](sqltdecertificate.md) | `sqltdecertificates` | Transparent Data Encryption certificates and the database master key. |

Each page opens with a copy-pasteable minimal manifest, then documents every field.

Live per-item detail (per-database TDE state, per-procedure deployment, login/role membership, SQL
Agent run history, AG replica health) is **not** stored in CRD status. It is served on demand by the
[aggregated read-only view API](view-api.md) (`view.mssql-operator.evariops.com`), queryable with plain
`kubectl get tdedatabasestates` / `storedproceduredeployments` / `sqlusertargets` / `sqlagentjobruns` /
`agreplicastates`.

The authoritative schema for each resource is the CRD YAML shipped in the Helm chart under
[`charts/mssql-operator/templates/crds/`](../../charts/mssql-operator/templates/crds/). These pages
describe and illustrate it. A drift test generates the CRD YAML from the C# entity types and keeps the
two in sync, so they never disagree.
