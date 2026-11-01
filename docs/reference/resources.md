# Resource reference

This page describes every resource of the API `mssql-operator.evariops.com/v1alpha1`: an example, the main fields, the annotations and the status. The complete schema, with every field and its description, is available with `kubectl explain`:

```bash
kubectl explain sqlavailabilitygroup.spec --recursive
```

The CRDs and their validations are in [`charts/mssql-operator/templates/crds/`](../../charts/mssql-operator/templates/crds/). Every resource also has `spec.automation` ([Operations](../guide/operating.md#automation-modes-and-approvals)) and a `NeedsAttention` condition ([Failure modes](../failure-modes.md#triage)). All annotations use the prefix `mssql-operator.evariops.com/`.

- [SqlInstance](#sqlinstance)
- [SqlAvailabilityGroup](#sqlavailabilitygroup)
- [SqlDatabase](#sqldatabase)
- [SqlAvailabilityGroupDatabase](#sqlavailabilitygroupdatabase)
- [SqlUser](#sqluser)
- [SqlConfiguration](#sqlconfiguration)
- [SqlAgentJob](#sqlagentjob)
- [SqlStoredProcedure](#sqlstoredprocedure)
- [SqlTdeCertificate](#sqltdecertificate)
- [SqlControlRecord](#sqlcontrolrecord)

## SqlInstance

A standalone SQL Server: one pod in a StatefulSet, its volumes and a Service.

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlInstance
metadata: { name: reporting, namespace: sql }
spec:
  version: "2022"
  edition: Developer             # Developer (default), Express, Standard, Enterprise
  saPasswordSecretRef: { name: reporting-sa, key: password }
  memory: { total: 8Gi }
  storage:
    data: { size: 20Gi }
    log:  { size: 10Gi }
```

The operator creates a StatefulSet `<name>` with the pod `<name>-0`, a Service `<name>` (clients connect to `reporting.sql.svc.cluster.local:1433`), and the key Secrets described in [Key material](../guide/protecting-data.md#key-material).

### Fields

| Field | Default | Description |
|---|---|---|
| `version` | `2022` | `2019`, `2022` or `2025`. |
| `edition` | `Developer` | Selected at runtime (`MSSQL_PID`). Cannot be changed in place. |
| `cumulativeUpdate` / `baseOs` | operator default | Exact build (`CU25`) and OS base (`ubuntu-22.04`). See [SQL Server builds](../guide/operating.md#sql-server-builds). |
| `image` / `imageTag` | | Private mirror. Disables the build checks. |
| `saPasswordSecretRef` | required | Secret holding the `sa` password. Changing the Secret rotates the password. |
| `memory.total` | | Pod memory (requests equal to limits), at least 4Gi. `max server memory` is derived from it, minus `systemReserve` (`2Gi`). |
| `resources` | | CPU requests and limits. `cpuPinning: true` pins whole CPUs. |
| `storage.<volume>` | `10Gi` | `data`, `log`, `backup`, and optionally `sysDb`, `tempDb`, `errorLogs`, `certBackups`. Sizes can only grow, and optional volumes cannot be added or removed later. |
| `service` | `ClusterIP`, `1433` | Service type, port and annotations. |
| `configurationRef.name` | | `SqlConfiguration` to apply. |
| `tls.secretName` / `tls.forceEncryption` | | Certificate presented to clients. |
| `machineKeySecretRef` | generated | Existing machine key to use. |
| `podAnnotations`, `nodeSelector`, `tolerations`, `priorityClassName`, `runtimeClassName` | | Pod placement and runtime. |
| `updateStrategy.type` | `RollingUpdate` | `OnDelete` restarts the pod only when it is deleted. |
| `podDisruptionBudget.enabled` | `false` | PDB with `maxUnavailable: 0`: node drains wait until the pod is moved manually. |
| `networkPolicy` | disabled | CiliumNetworkPolicy defining who can reach port 1433 and which destinations the pod can reach. |

### Status

`kubectl get sqlinstance` shows the phase, the version, the CU and the running build. While `status.productVersion` differs from `status.cumulativeUpdate`, an upgrade is still in progress. `status.memory.aligned` indicates whether SQL Server's memory settings match the pod. `NeedsAttention` is raised when the machine key Secret was replaced or is missing, when SQL Server refuses the `sa` password from the Secret, and when the pod is stuck on a lost node (`InstanceOnLostNode`).

## SqlAvailabilityGroup

An Always On availability group with `CLUSTER_TYPE = NONE`: N replicas in a StatefulSet, the AG between them, and Services for the primary and the readable secondaries. See [Availability groups](../guide/availability-groups.md).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata: { name: orders, namespace: sql }
spec:
  edition: Enterprise
  replicas:
    count: 3
    synchronousCount: 3
    durability: Quorum
  saPasswordSecretRef: { name: orders-sa, key: password }
  masterKeyPasswordSecretRef: { name: orders-dmk, key: password }
  hadrCertificate: { source: Generate, model: PerReplica }
  instanceTemplate:
    version: "2022"
    memory: { total: 16Gi }
    storage:
      data: { size: 50Gi }
      log:  { size: 20Gi }
  listener:
    service: { type: ClusterIP }
  readOnlyListener:
    service: { type: ClusterIP }
```

The operator creates a StatefulSet `<name>` (pods `<name>-0` to `<name>-N`), the Services `<name>-listener` (read/write), `<name>-ro` (read-only) and `<name>-headless` (one DNS name per replica for the endpoints), a PodDisruptionBudget `<name>-pdb`, and a `SqlControlRecord` named `ag-<name>`.

### Fields

| Field | Default | Description |
|---|---|---|
| `edition` | `Enterprise` | `Enterprise`, `Developer` (2 to 9 replicas) or `Standard` (exactly 2, basic AG). |
| `replicas.count` | `3` | Number of replicas. Immutable. |
| `replicas.synchronousCount` | `2` | Replicas in synchronous commit, the primary included. Only these are automatic failover targets. |
| `replicas.durability` | see description | `Quorum` or `Legacy`. When not set: `Quorum` for a new Enterprise AG with `synchronousCount` of 3 or more, `Legacy` otherwise. `Quorum` requires a synchronous secondary (`synchronousCount` of 2 or more) and is refused on Standard. With a single one, writes block whenever it is down. |
| `replicas.requiredSyncFloor` | `0` | With `Legacy`, minimum value of `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT`. |
| `replicas.overrides[]` | | Per replica (`ordinal`): availability mode, readable secondary, backup priority, session timeout. |
| `replicaDefaults` | | Same settings for all replicas. `sessionTimeout` defaults to 10 seconds. |
| `saPasswordSecretRef` | required | `sa` password of every replica. |
| `masterKeyPasswordSecretRef` | required | Database master key password (endpoint certificates, TDE). |
| `hadrCertificate` | required | Endpoint authentication. Only `source: Generate`, `model: PerReplica` is implemented. |
| `instanceTemplate` | | Same settings as a `SqlInstance`: `version`, `cumulativeUpdate`, `memory`, `resources`, `storage`, `tls`, `podAnnotations`, `configurationRef`, and so on. A change triggers a rolling update. |
| `listener` / `readOnlyListener` | | Service type, port and annotations. `readOnlyListener.fallbackToPrimary` (`true`) sends reads to the primary when no secondary qualifies. The read-only listener is Enterprise only. |
| `readOnlyRouting.enabled` | `false` | SQL Server read-only routing for `ApplicationIntent=ReadOnly` clients. |
| `backupPreference` | | Automated backup preference of the AG, for backup scripts. |
| `failover.primaryUnreachableThreshold` | `3` | Health passes without a primary before a failover, with a minimum of 20 seconds. |
| `failover.healthCheckIntervalSeconds` | `10` | Interval between health passes. |
| `failover.allowAutomaticDataLossFailover` | `false` | Allows the operator to force a failover that can lose data when no lossless target exists. |
| `failover.maxDataLossKb` | | Blocks the automatic failover with data loss, since the loss cannot be measured without the primary. |
| `failover.preferredTargets[]` | | Ordinals preferred among equally advanced candidates. |
| `failover.removeDisconnectedReplicaAfterMinutes` | `30` | Removes a replica that holds the log back (log 85 % full), and adds it back when it returns. `0` disables. |
| `failover.splitBrainProtection.fencingTimeoutSeconds` | `30` | Maximum wait for VolumeAttachments to disappear when fencing a primary on a NotReady node. |
| `updateStrategy.rollingUpdate.pauseBetweenPodsSeconds` | `60` | Pause between two replicas during a rolling update. |
| `volumeAutoGrow.mode` | `Auto` | Grows data and log volumes online at 80 % usage, up to 4 times their declared size. |
| `topologySpread` / `antiAffinity` | spread over nodes and zones, preferred | Replica placement. |
| `networkPolicy` | disabled | CiliumNetworkPolicy for the replicas. |

### Annotations

| Annotation | Effect |
|---|---|
| `failover-to: <replica>` | Planned, lossless failover. Removed when complete, or after 3 failed attempts. |
| `force-failover-to: <replica>` with `allow-data-loss: "true"` | Forced failover accepting data loss. Removed before execution. |
| `approve: <action id>` | Approves a withheld destructive action (ids in `kubectl get operatorplans`). |
| `retry-rolling-update: "true"` | Resumes a rolling update stopped in phase `Failed`. |
| `approve-engine-upgrade: <CU>` | Applies a new default CU brought by an operator upgrade. The value is that CU (`CU10`); any other value leaves the running build in place. |
| `allow-version-downgrade: "true"` | Accepts an older build. |
| `safety-level: "1"` | Safety level of the AG, capped by `operator.safetyLevelMax`. |
| `reconcile-paused: "true"` | Stops reconciliation of the AG: no health check, no failover, no breakout action. |
| `allow-privileged-roles: "true"` | Allows `SqlUser` resources to grant `sysadmin`, `securityadmin` and `serveradmin` on this AG. |
| `allow-sa-deployments: "true"` | Allows `SqlStoredProcedure` resources to deploy as `sa` on this AG. |

### Status

`kubectl get sqlavailabilitygroup` shows the phase, the primary, the CU and the running build. `status.phase` is one of `Pending`, `Initializing`, `Running`, `Degraded`, `FailingOver` or `Failed`. `status.replicas[]` gives the role, availability mode, synchronization state and connection state of each replica, `status.lastFailover` the time, source, target, reason and data loss of the last failover, and `status.rollingUpdate` the progress of a rollout. The main conditions are `NeedsAttention` (split brain, no lossless target, failed rolling update, full volume, replica on a lost node), `DurabilityAtRisk` (the AG runs without its durability guarantee) and `ReplicasColocated`. `kubectl get agreplicastates` shows the live state of the replicas.

## SqlDatabase

A user database on a `SqlInstance` or a `SqlAvailabilityGroup`, created when missing, adopted when it exists, with the declared options. Short name `sqldb`. See [Databases](../guide/databases-and-access.md#databases).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata: { name: orders-db, namespace: sql }
spec:
  availabilityGroupRef: { name: orders }     # or instanceRef: { name: reporting }
  databaseName: Orders
  collation: French_CI_AS
  compatibilityLevel: 160
  owner: orders_owner
  files:
    data: { size: 10Gi, growth: 1Gi }
    log:  { size: 2Gi, growth: 512Mi }
  availabilityGroup:
    logBackup: { mode: Local, retentionHours: 72 }
  deletionPolicy: Retain
```

### Fields

| Field | Default | Description |
|---|---|---|
| `instanceRef.name` / `availabilityGroupRef.name` | one is required | Server hosting the database, in the same namespace. Immutable. |
| `databaseName` | required | Immutable. System databases are refused. |
| `collation` | server default | Applied at creation. Immutable. |
| `recoveryModel` | unchanged | `Full`, `Simple` or `BulkLogged`. Always `Full` in an AG. |
| `compatibilityLevel` | unchanged | 100 to 170. |
| `owner` | unchanged | Owning login, applied once the login exists. |
| `files.data` / `files.log` | unchanged | `size` (can only grow), `growth` (`256Mi` or `10%`), `maxSize` (or `Unlimited`). A size larger than the free space of the volume is not attempted. |
| `availabilityGroup` | | `seedingMode`, `logBackup`, `initialization`, `dataMovement`, copied to the `SqlAvailabilityGroupDatabase`. Only the blocks that are set are enforced. |
| `deletionPolicy` | `Retain` | `Retain` keeps the database and its AG membership. `Delete` removes it from the AG and drops it on every replica, if this resource owns it. |

### Status

`status.phase` is one of `Pending` (target not running), `Creating`, `Joining` (on an AG, not synchronized yet), `Ready`, `Conflict` (owned by another `SqlDatabase`) or `Failed` (retried every minute). `status.collation`, `recoveryModel`, `compatibilityLevel` and `owner` are read from the server, and `status.adopted` indicates whether the database existed before. On an AG, `status.availabilityGroupDatabase` and `availabilityGroupPhase` give the managed `SqlAvailabilityGroupDatabase` and its phase. The conditions are `Ready`, `OptionsApplied` (false when an option was refused or a file does not fit), `OwnerLoginMissing`, `CollationMismatch` (adopted database with a different collation), `OrphanCopy` and `LogBackupModeDeprecated` (`availabilityGroup.logBackup.mode` not set).

## SqlAvailabilityGroupDatabase

Adds a database to an availability group and keeps it synchronized on every replica. Short name `sagdb`. A `SqlDatabase` on an AG creates and manages this resource (annotation `mssql-operator.evariops.com/sqldatabase`), and changes should then be made on the `SqlDatabase`. Creating one directly is intended for databases created by other means.

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroupDatabase
metadata: { name: orders-db, namespace: sql }
spec:
  availabilityGroupRef: { name: orders }
  databaseName: Orders
  logBackup:
    mode: Local
    retentionHours: 72
```

The database must exist on the primary. The operator takes the initialization backup if the database has none, adds it to the AG and seeds the secondaries.

### Fields

| Field | Default | Description |
|---|---|---|
| `availabilityGroupRef.name` | required | The AG. Immutable. |
| `databaseName` | required | Immutable. System databases are refused. Converted to FULL recovery if needed (`RecoveryModelConverted`). |
| `seedingMode` | `Automatic` | `Automatic`: the primary copies the database to the secondaries. `Manual`: the database is restored on the secondaries manually. |
| `logBackup.mode` | not set (deprecated) | `Local` or `Disabled`. See [Log backups](../guide/protecting-data.md#log-backups-and-the-log-chain). |
| `logBackup.thresholdPercent` | `50` | Log usage that triggers an operator log backup. |
| `logBackup.retentionHours` | none in `Local` mode | Deletes the operator's own log backups after this delay. |
| `initialization.backupWindow` | none | Daily window (`start` in UTC, `durationMinutes`) for the initialization backup. |
| `dataMovement.suspended` | `false` | Suspends data movement (`SET HADR SUSPEND`). |
| `dataMovement.requireManualResumeAfterDataLoss` | `true` | After a failover with data loss, divergent replicas rejoin only with `approve-data-loss-resume`. |

### Annotations

| Annotation | Effect |
|---|---|
| `reseed-replica: <replica>` | Re-seeds that replica now. Removed immediately; a failed re-seed publishes `ReseedFailed`. |
| `approve-data-loss-resume: "true"` | Lets the divergent replicas of the last failover with data loss rejoin, discarding their extra commits. Removed once used. |

### Status

`status.phase` is one of `Pending`, `WaitingForAg`, `Adding`, `Seeding`, `Synchronizing`, `Synchronized`, `Suspended`, `NotSynchronizing`, `Removing` or `Failed` (retried every minute). `status.replicaStates[]` gives the state of the database on each replica. `NeedsAttention` is raised for `TransactionLogFull` (the message gives what holds the log), `ReseedBlocked` and `ManualRejoinRequired`. `LogChainHeldByOperator` indicates that the operator's log backups are part of the restore chain. `kubectl get logchains` lists the backups recorded on each replica. Deleting the resource removes the database from the AG, and the database remains on the primary.

## SqlUser

A login, its database users, roles and permissions, on one or more servers. See [Logins and permissions](../guide/databases-and-access.md#logins-and-permissions).

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
  expiration:
    ttl: 30d
    action: Disable
```

### Fields

| Field | Default | Description |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, in the same namespace. |
| `login.name` | required | Login name, also used for the database users. |
| `login.passwordSecretRef` | | Password Secret. Changing the Secret rotates the password. |
| `login.defaultDatabase`, `checkPolicy`, `checkExpiration` | | `CREATE LOGIN` options. |
| `databasePermissions[]` | | `databases` (or an anchored `databaseFilter` pattern), `roles`, `schemaPermissions`. |
| `serverRoles` / `serverPermissions` | | Fixed server roles and allow-listed server permissions. Privileged roles require the target annotation `allow-privileged-roles`. |
| `overrides[]` | | Different permissions for one `instance` or `availabilityGroup`. |
| `expiration` | | `expiresAt` (RFC 3339) or `ttl` (`12h`, `30d`); `action: Disable` or `Delete`; `terminateSessions` (`true`). |

### Status

`status.phase` is one of `Pending`, `Ready`, `Expired` or `Failed`. `kubectl get sqlusertargets` shows the live state per target and per database. The annotation `mssql-operator.evariops.com/force-finalize: "true"` lets a deletion complete when a target no longer exists.

## SqlConfiguration

Server-wide settings for the instances and AGs it targets. See [Server settings](../guide/databases-and-access.md#server-settings).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlConfiguration
metadata: { name: oltp-defaults, namespace: sql }
spec:
  targetMatchLabels: { tier: oltp }
  spConfigure:
    maxDegreeOfParallelism: 4
    costThresholdForParallelism: 50
    optimizeForAdHocWorkloads: true
  traceFlags:
    global: [3226]
  tempDb:
    dataFiles: 8
```

A configuration applies to every `SqlInstance` whose `spec.configurationRef.name` names it, to every AG whose `spec.instanceTemplate.configurationRef.name` names it (on all replicas), and to every instance or AG in the namespace whose labels match `targetMatchLabels`.

### Fields

| Field | Description |
|---|---|
| `spConfigure.<option>` | Typed options. Options that are not set are left unchanged. Memory is not set here for servers that declare `memory`. |
| `spConfigure.advanced[]` | Any other allow-listed option, by `name` and `value`. Options that allow code execution require `allowDangerous: true`. |
| `traceFlags.global` | Exact set of global trace flags: missing flags are enabled, others disabled. |
| `traceFlags.startup` | Startup trace flags, written to `mssql.conf` and applied at the next restart. |
| `tempDb` | Number and size of tempdb data files. Files are added, never removed. |
| `serverSettings.defaultDataPath` / `defaultLogPath` / `defaultBackupPath` | Written to `mssql.conf` and applied at the next restart. |
| `databaseMail` | Profiles and SMTP accounts, with passwords from Secrets. Requires an egress rule when a network policy is enabled. |
| `agentOperators[]` | SQL Agent operators, used by job notifications. |

### Status

`status.phase` is one of `Pending`, `Applied`, `PartiallyApplied` or `Failed`. `PendingRestart` lists the options waiting for a restart; the operator does not restart SQL Server to apply them. Deleting the configuration, or no longer targeting a server, restores the previous values.

## SqlAgentJob

A SQL Server Agent job, kept identical on the servers it targets. See [Agent jobs](../guide/databases-and-access.md#agent-jobs).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAgentJob
metadata: { name: nightly-stats, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  job:
    name: nightly-stats
    allowDangerous: true            # owner defaults to sa: the steps run as sysadmin
    steps:
      - name: Update statistics
        database: Orders
        command: EXEC sp_updatestats;
    schedules:
      - name: Every night at 02:30
        frequencyType: Daily
        startTime: "023000"         # HHmmss
```

### Fields

| Field | Default | Description |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, in the same namespace. |
| `job.name` | required | Job name in msdb. |
| `job.owner` | `sa` | Owning login. A privileged owner requires `allowDangerous: true`. |
| `job.allowDangerous` | `false` | Required for a sysadmin-owned job, and for `CmdExec`, `PowerShell`, `SSIS` or `SSAS` steps. |
| `job.enabled` | `true` | |
| `job.steps[]` | required | `name`, `command`, `database` (`master`), `type` (`TSQL`), `onSuccess` / `onFailure`, retries. The last step quits with success by default. |
| `job.schedules[]` | | `frequencyType` (`Once`, `Daily`, `Weekly`, `Monthly`), `frequencyInterval`, `subdayType` / `subdayInterval`, `startTime`, `startDate`. |
| `job.notifications` | | `onFailure` / `onCompletion`: an Agent operator and `Email` or `Pager`. |

### Status

`kubectl get sqlagentjob` shows the phase and the number of configured targets. `kubectl get sqlagentjobruns` reads the run history from msdb. Deleting the resource deletes the job on every target.

## SqlStoredProcedure

Stored procedures deployed into the databases of the servers it targets, from inline T-SQL or from `.sql` files in a GitHub repository. See [Stored procedures](../guide/databases-and-access.md#stored-procedures).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlStoredProcedure
metadata: { name: orders-procs, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  databases: [Orders]
  inline:
    - name: usp_Ping
      definition: |
        CREATE OR ALTER PROCEDURE dbo.usp_Ping AS SELECT 1 AS ok;
```

### Fields

| Field | Default | Description |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, in the same namespace. |
| `databases` / `databaseFilter` | | Database names, or a `pattern` with `exclude`. |
| `inline[]` | | `name`, `schema` (`dbo`), `definition`. |
| `github` | | `repository` (`owner/repo`), `ref.tag` or `ref.sha`, `paths[]`, `include` / `exclude` globs, `authSecretRef`. |
| `sync.intervalMinutes` | `30` | Check interval for GitHub sources. |
| `sync.followLatestRelease` / `releaseFilter` | `false` | Deploys the latest matching release. |
| `deploymentOptions.transactional` | `true` | Applies the changed procedures of a database in one transaction. |
| `deploymentOptions.dryRun` | `false` | Plans without deploying. |
| `deploymentOptions.deployAs` | `LeastPrivilege` | Deploys through a `db_ddladmin`-only login. `Sa` requires the target annotation `allow-sa-deployments`. |

### Status

`kubectl get sqlstoredprocedure` shows the deployed and desired counts, and `kubectl get storedproceduredeployments` the state per database. Deleting the resource drops the procedures it deployed.

## SqlTdeCertificate

Transparent data encryption for the databases of the servers it targets: database master key and certificate on every server (every replica of an AG), database encryption, certificate backups and rotation. See [Transparent data encryption](../guide/protecting-data.md#transparent-data-encryption).

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlTdeCertificate
metadata: { name: orders-tde, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  certificate:
    logicalName: orders-tde
    source:
      type: OperatorManaged
      operatorManaged:
        expiryDate: "2030-01-01"
  databases:
    selector:
      names: [Orders]                # or allUserDatabases: true
    policy: {}                       # AES_256, encrypts databases that are not encrypted yet
```

### Fields

| Field | Default | Description |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, in the same namespace. |
| `certificate.logicalName` | required | Base of the certificate and Secret names. |
| `certificate.source.type` | `OperatorManaged` | `OperatorManaged`: generated and stored in `<logicalName>-<version>`. `KubernetesSecret`: imported from Secrets holding `certificate.cer`, `private-key.pvk` and `password`. |
| `certificate.archiveToSecret` | | Also archives each certificate in an immutable Secret. |
| `certificate.backupToS3` | | Also uploads an encrypted, self-contained bundle to S3-compatible storage. |
| `databases.selector` | required | `allUserDatabases`, `names`, `namePatterns`, with exclusions. |
| `databases.policy.encryptionAlgorithm` | `AES_256` | `AES_128`, `AES_192` or `AES_256`. |
| `databases.policy.nonEncryptedDatabasePolicy` | `Encrypt` | `Encrypt`, `Skip`, `Warn` or `Fail`. |
| `databases.policy.maxConcurrentEncryptionScans` | `1` | Concurrent encryption scans per server. |
| `rotation` | none | `trigger` (`ManualOnly`, `ManualOrInterval`, `OnSourceCandidate`), `intervalDays` (`365`), `mode` (`ProtectorOnly`, `ProtectorAndDekRegenerate`). |

The annotation `mssql-operator.evariops.com/rotate: "true"` starts a rotation. The operator never drops a certificate, and never protects the database master key with `sa`.

### Status

`kubectl get sqltdecertificate` shows the encrypted and pending counts. `status.rotationPhase` is one of `Idle`, `Preparing`, `Rotating`, `RetentionWaiting` or `Failed`. The conditions `CertificateExpiry` and `CertificateAge` warn before the certificate expires or gets old. `kubectl get tdedatabasestates` shows the state of each database.

## SqlControlRecord

Written by the operator only. There is one per AG (`ag-<name>`) and one per instance (`instance-<name>`), in the same namespace. It holds the safety state that must survive the deletion and re-creation of the resource, and has no owner reference. See [Control record and intents](../architecture/safety.md#control-record-and-intents).

```console
$ kubectl get sqlcontrolrecords -n sql
NAME        OWNER    EPOCH   INTENT   STAGE   AGE
ag-orders   orders   12                       30d
```

| Field | Content |
|---|---|
| `spec.epoch` | Epoch of the last leader that wrote the record. Never decreases (CEL rule). |
| `spec.intent` | Failover in progress, its stage and its deadline. Empty between operations. |
| `spec.durability` | Durability profile, recorded once reached. |
| `spec.openDivergences` | Failovers with data loss whose divergent replicas have not been resolved. |
| `spec.keys.machineKeySha256` | Fingerprint of the machine key the data was created with. |
| `spec.reconstructedAt` | Set when the record was rebuilt from SQL Server after being lost. |

The record is deleted once the resource and its volumes are gone. It should not be edited or deleted manually. A lost record is rebuilt from SQL Server, and no destructive action runs until it is written again.
