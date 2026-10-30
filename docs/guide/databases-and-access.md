# Databases, logins and server objects

This guide covers the resources that manage objects inside SQL Server: `SqlDatabase`, `SqlUser`, `SqlConfiguration`, `SqlAgentJob` and `SqlStoredProcedure`. They share three behaviours: the operator changes only what the spec declares, it records what it owns in SQL Server itself, and a resource deleted and re-created by a GitOps tool takes back what its predecessor owned.

## Targets

`SqlUser`, `SqlAgentJob`, `SqlStoredProcedure` and `SqlTdeCertificate` select their servers with `spec.target`:

```yaml
spec:
  target:
    instances: [reporting]          # SqlInstance names
    availabilityGroups: [orders]    # SqlAvailabilityGroup names
    matchLabels: { tier: oltp }     # or labels on those resources
```

Targets are always resolved in the resource's own namespace ([Security model](../architecture/security.md#trust-boundaries)). A `SqlDatabase` names a single server with `instanceRef` or `availabilityGroupRef`. A `SqlConfiguration` is selected by the server's `configurationRef`, or selects servers with `targetMatchLabels`.

On an availability group, server-level objects are applied on every replica, because SQL Server does not replicate them: logins (with the same SID on every replica), configuration, Agent jobs. Objects inside a database, such as database users and procedures, are applied on the primary and replicated by the AG.

## Databases

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata: { name: orders-db, namespace: sql }
spec:
  availabilityGroupRef: { name: orders }     # or instanceRef: { name: reporting }
  databaseName: Orders
  collation: French_CI_AS                    # applied at creation only
  compatibilityLevel: 160
  owner: orders_owner                        # a login, for example created by a SqlUser
  files:
    data: { size: 10Gi, growth: 1Gi }
    log:  { size: 2Gi, growth: 512Mi }
  deletionPolicy: Retain                     # default
```

### Creation and adoption

A missing database is created. An existing one is adopted (`status.adopted: true`, event `DatabaseAdopted`), and the operator changes only the options declared in the spec. The collation of an existing database is never changed; a different declared collation is reported with `CollationMismatch`.

The owning resource is recorded in the database as the extended property `mssqlop.owner`, which follows the database through failovers. Depending on that property:

- a database without an owner is adopted;
- a database owned by a deleted `SqlDatabase` of the same name, as after a GitOps re-creation, is taken back;
- a database owned by another existing `SqlDatabase` is left untouched, and the new resource goes to phase `Conflict`.

### Options and files

The operator converges `recoveryModel` (always FULL in an AG), `compatibilityLevel` and `owner` (applied once the login exists), and the `size`, `growth` and `maxSize` of the primary data file and of the log file. Options that are not declared are not changed. File sizes only grow. A size larger than the free space of the volume is not attempted; the option is reported with `OptionsApplied=False` and the event `OptionsNotApplied`. Each option is applied separately, so a refused option does not block the others.

### Deletion

With `deletionPolicy: Retain` (default), deleting the resource keeps the database and its AG membership, and a new `SqlDatabase` with the same name takes it back. With `deletionPolicy: Delete`, the database is removed from the AG and dropped on every replica, only if this resource owns it. The drop goes through the [actuation gateway](../architecture/safety.md#actuation-gateway) as a destructive action, so it waits in `Plan` and `Observe` modes.

### On an availability group

The operator creates the database on the primary, then a [`SqlAvailabilityGroupDatabase`](../reference/resources.md#sqlavailabilitygroupdatabase) named after the `SqlDatabase`. That resource takes the initialization backup, adds the database to the AG and seeds the secondaries. Changes go to the `SqlDatabase`: the blocks of `spec.availabilityGroup` (`seedingMode`, `logBackup`, `initialization`, `dataMovement`) are copied to the managed resource, and only the blocks that are set are enforced.

A database created by other means, such as a restore or an application installer, can join an AG through a `SqlAvailabilityGroupDatabase` alone. A Standard edition AG holds a single database.

If a failover happens between the creation of the database and its join, an empty copy left on the former primary is backed up and dropped, and the database is created again on the new primary. A copy that already contains data is never dropped ([Failure modes](../failure-modes.md#failover-between-the-creation-of-a-database-and-its-join)).

## Logins and permissions

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlUser
metadata: { name: orders-app, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  login:
    name: orders_app
    passwordSecretRef: { name: orders-app-password, key: password }
  databasePermissions:
    - databases: [Orders]
      roles: [db_datareader, db_datawriter]
      schemaPermissions:
        - schema: reporting
          permissions: [SELECT]
```

A `SqlUser` manages one SQL login, its database users, and their roles and grants on every target. On an AG, the login exists on every replica with the same SID, and the database users are created on the primary. Updating the password Secret changes the password on every target at the next reconcile.

### Expiration

```yaml
spec:
  expiration:
    ttl: 8h                 # or expiresAt: "2026-11-30T18:00:00Z"
    action: Disable         # or Delete: drops the database users and the login
    terminateSessions: true
```

At expiry, the login is disabled or dropped and its sessions are killed. Moving `expiresAt` to a later date re-enables a disabled login.

### Server-level rights

`serverRoles` takes fixed server roles and `serverPermissions` takes server permissions from an allow-list. `sysadmin`, `securityadmin` and `serveradmin` are granted only on targets annotated `mssql-operator.evariops.com/allow-privileged-roles: "true"`. On other targets they are skipped and the `SqlUser` reports `PrivilegedRolesDenied`.

### Per-target overrides

An override replaces the top-level permission lists for one target:

```yaml
spec:
  overrides:
    - availabilityGroup: orders-staging
      databasePermissions:
        - databases: [Orders]
          roles: [db_owner]
```

### Ownership

Each server records in `msdb.mssqlop` which `SqlUser` owns each login. A second `SqlUser` that declares the same login is refused with `LoginOwnedByAnotherResource`, and the password is left unchanged. A `SqlUser` re-created by a GitOps tool takes its login back and revokes the permissions its spec no longer grants. An existing login with no recorded owner is taken over by the first `SqlUser` that declares it. Deleting a `SqlUser` kills the login's sessions, then drops its database users and the login on every target. `kubectl get sqlusertargets` shows the live state of the login and its users on each target.

## Server settings

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlConfiguration
metadata: { name: oltp-defaults, namespace: sql }
spec:
  targetMatchLabels: { tier: oltp }
  spConfigure:
    maxDegreeOfParallelism: 4
    costThresholdForParallelism: 50
    backupCompressionDefault: true
  traceFlags:
    global: [3226]
  tempDb:
    dataFiles: 8
```

A `SqlConfiguration` manages `sp_configure` options (typed fields, or any allow-listed option under `spConfigure.advanced`), global and startup trace flags, tempdb files, default paths, Database Mail profiles and Agent operators. On an AG it applies to every replica.

Before changing an option, the operator records its previous value in `msdb.mssqlop` on that server. When the option is removed from the spec, when the configuration is deleted, or when it stops targeting a server, the previous value is restored and the trace flags it enabled are disabled. This also works when the resource is re-created by a GitOps tool.

Options that allow code execution (`xp_cmdshell`, `clr enabled`…) require `allowDangerous: true`. `max server memory` is derived from `memory.total` on servers that declare it, so `maxServerMemoryMb` should be left unset for them. Options that take effect only after a restart are reported with `PendingRestart`, and the operator does not restart SQL Server to apply them.

## Agent jobs

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAgentJob
metadata: { name: nightly-stats, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  job:
    name: nightly-stats
    owner: stats_runner              # non-privileged login; the default owner sa requires allowDangerous
    steps:
      - name: Update statistics
        database: Orders
        command: EXEC sp_updatestats;
    schedules:
      - name: Every night at 02:30
        frequencyType: Daily
        startTime: "023000"          # HHmmss
```

A job owned by `sa` (the default) runs its steps as sysadmin. The webhook requires `job.allowDangerous: true` in that case, or a non-privileged `owner`. `CmdExec` and `PowerShell` steps require the same flag.

msdb is not part of the AG, so on an AG the job is created on every replica, with a first step that ends the job on replicas that are not primary. The job therefore runs on the current primary, including after a failover. `kubectl get sqlagentjobruns` reads the run history from msdb. Deleting the resource deletes the job on every target.

## Stored procedures

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlStoredProcedure
metadata: { name: orders-procs, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  databases: [Orders]
  github:
    repository: my-org/orders-db
    authSecretRef: { name: github-token, key: token }   # private repositories
    paths: [{ path: procedures/ }]
  sync:
    followLatestRelease: true
    releaseFilter: "v*"
```

Procedures come from inline T-SQL or from `.sql` files in a GitHub repository, pinned with `ref.tag` or `ref.sha`, or following the latest matching release. They are deployed with `CREATE OR ALTER`. Only procedures whose definition changed are redeployed (compared by hash), and the changed procedures of a database are applied in a single transaction.

Deployments use an operator-managed login that is only `db_ddladmin` on the targeted databases (`deployAs: LeastPrivilege`, the default). Deploying as `sa` (`deployAs: Sa`) requires the annotation `mssql-operator.evariops.com/allow-sa-deployments: "true"` on the target. GitHub sources need egress to `api.github.com`, which the operator's network policy allows by default.

`kubectl get storedproceduredeployments` shows the deployment state of each procedure in each database. Deleting the resource drops the procedures it deployed.
