# Tenancy model

## Namespace = trust boundary

The operator treats the Kubernetes namespace as the tenancy boundary:

- **Target resolution never leaves the CR's namespace.** A `SqlUser`, `SqlConfiguration`,
  `SqlStoredProcedure`, `SqlAgentJob` or `SqlTdeCertificate` can only act on
  `SqlInstance`/`SqlAvailabilityGroup` resources living in the *same* namespace.
  Cross-namespace targeting was removed by design: it allowed any tenant able
  to create a CR in their own namespace to create logins — including privileged ones — on
  another tenant's instances, bypassing Kubernetes RBAC entirely.
- **Privileged server roles require target opt-in.** Even inside a namespace, granting
  `sysadmin`, `securityadmin` or `serveradmin` through a `SqlUser` requires the annotation
  `mssql.operator/allow-privileged-roles: "true"` on the target SqlInstance/SqlAvailabilityGroup.
  The check runs at reconcile time (the admission webhook only warns — it can be disabled).

## Shared instances break isolation

A single SQL Server instance shared between teams is **not** isolated by the operator:

- The ViewAPI providers (`sqlusertargets`, `agreplicastates`, …) enumerate the databases of
  the instance, filtered by each CR's `databaseFilter`. A wide wildcard exposes the *names*
  of other tenants' databases to anyone allowed to read those view resources.
- `SqlConfiguration` applies instance-global settings (`sp_configure`, trace flags, tempdb):
  whoever can create one in the instance's namespace controls the whole instance.

**Recommendations:**

1. One instance (or AG) per tenant, in the tenant's namespace — the supported isolation model.
2. If an instance must be shared, scope every CR with explicit database names rather than
   filter wildcards, and treat the namespace as a single trust zone.
3. Do not bind the `view-reader` ClusterRole broadly; prefer namespaced RoleBindings so a
   tenant can only read view resources in their own namespace.

## Migration note (v1alpha1 breaking change)

`target.namespaces` was removed from all CRD schemas. After upgrading the CRDs, any stored
CR still carrying that field will have it pruned silently by the API server, and resolution
falls back to the CR's namespace. If you relied on cross-namespace targeting, move the CR
into the namespace of the instance it targets.
