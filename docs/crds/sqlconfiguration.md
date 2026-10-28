# `SqlConfiguration`

> Declarative server-level configuration (sp_configure settings, trace flags, tempdb layout, Database Mail and Agent operators) applied to the SQL Server instances it targets.

`apiVersion: mssql-operator.evariops.com/v1alpha1` · `kind: SqlConfiguration` · scope: Namespaced · plural: `sqlconfigurations`

## Minimal example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlConfiguration
metadata:
  name: my-sql-config
  namespace: mssql-demo
spec:
  targetMatchLabels:
    mssql-operator.evariops.com/instance: my-sql-instance
  spConfigure:
    maxDegreeOfParallelism: 4
    costThresholdForParallelism: 50
```

## Overview

A `SqlConfiguration` describes a set of instance-wide SQL Server settings and applies them to every `SqlInstance` it matches in the same namespace. On reconcile the operator connects to each targeted instance and applies `sp_configure` values (memory, MAXDOP, cost threshold, and assorted boolean and advanced options), enables or disables global trace flags, grows the tempdb to the requested file layout, and optionally configures Database Mail and SQL Agent operators. Settings are applied idempotently, so a value changes only when it differs from the instance's current configuration. The matched instances and the overall result are written back to the resource `status`.

## Spec

### Top-level fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `targetMatchLabels` | map[string]string | No |   | Label selector. An instance is targeted when **all** key/value pairs match the labels on the `SqlInstance` metadata. (An instance is also targeted when its own `spec.configurationRef.name` points at this resource. See Validation & behaviour.) |
| `spConfigure` | object | No |   | `sp_configure` server options. See [spConfigure](#spconfigure). |
| `traceFlags` | object | No |   | Global and startup trace flags. See [traceFlags](#traceflags). |
| `tempDb` | object | No |   | tempdb file layout. See [tempDb](#tempdb). |
| `modelDatabase` | object | No |   | `model` database defaults. See [modelDatabase](#modeldatabase). |
| `serverSettings` | object | No |   | Default paths, error-log and authentication settings. See [serverSettings](#serversettings). |
| `databaseMail` | object | No |   | Database Mail profiles and accounts. See [databaseMail](#databasemail). |
| `agentOperators` | array | No |   | SQL Agent operators. See [agentOperators](#agentoperators). |

### spConfigure

`sp_configure` server options. Any field left unset is not touched. Boolean values are translated to `0`/`1`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `spConfigure.maxServerMemoryMb` | int32 | No |   | `max server memory (MB)`. Ignored for instances that set `spec.memory` (a warning event is raised). |
| `spConfigure.minServerMemoryMb` | int32 | No |   | `min server memory (MB)`. Ignored for instances that set `spec.memory`. |
| `spConfigure.maxDegreeOfParallelism` | int32 | No |   | `max degree of parallelism`. |
| `spConfigure.costThresholdForParallelism` | int32 | No |   | `cost threshold for parallelism`. |
| `spConfigure.optimizeForAdHocWorkloads` | bool | No |   | `optimize for ad hoc workloads`. |
| `spConfigure.remoteAdminConnections` | bool | No |   | `remote admin connections`. |
| `spConfigure.backupCompressionDefault` | bool | No |   | `backup compression default`. |
| `spConfigure.defaultBackupChecksum` | bool | No |   | `default backup checksum` (declared on the spec, not applied by the current reconciler). |
| `spConfigure.containedDatabaseAuthentication` | bool | No |   | `contained database authentication`. |
| `spConfigure.clrEnabled` | bool | No |   | `clr enabled`. **Dangerous:** setting it to `true` requires `spConfigure.allowDangerous: true`. |
| `spConfigure.crossDbOwnershipChaining` | bool | No |   | `cross db ownership chaining`. **Dangerous:** setting it to `true` requires `spConfigure.allowDangerous: true`. |
| `spConfigure.allowDangerous` | bool | No | `false` | Explicit, review-visible opt-in required to *enable* the dangerous typed toggles (`clrEnabled`, `crossDbOwnershipChaining`, `remoteAdminConnections`). Disabling those options (`false`) never requires it. Mirrors the per-entry `allowDangerous` on `advanced[]`. See [security model](#dangerous-options--allowlist). |
| `spConfigure.advanced` | array | No |   | Escape hatch for arbitrary `sp_configure` options, gated by a three-tier allowlist. See [advanced](#spconfigureadvanced). |

#### spConfigure.advanced

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `spConfigure.advanced[].name` | string | Yes | `""` | Exact `sp_configure` option name (e.g. `fill factor (%)`). Must be in the allowlist (see below). |
| `spConfigure.advanced[].value` | int32 | Yes | `0` | Integer value to apply. |
| `spConfigure.advanced[].allowDangerous` | bool | No | `false` | Required (`true`) for options classified *dangerous* (e.g. `xp_cmdshell`, `clr enabled`, `Ole Automation Procedures`). Safe options apply without it; unknown options are always rejected. |

#### Dangerous options & allowlist

Setting an `sp_configure` option that enables code execution or crosses a trust boundary requires a deliberate, greppable, review-visible opt-in. `spConfigure.advanced[]` entries are classified into three tiers (`SpConfigureAllowlist`):

- **Safe** (e.g. `max degree of parallelism`, `fill factor (%)`, `max worker threads`) — applied directly.
- **Dangerous** (e.g. `xp_cmdshell`, `clr enabled`, `clr strict security`, `Ad Hoc Distributed Queries`, `Ole Automation Procedures`, `external scripts enabled`, `cross db ownership chaining`, `remote access`, `remote admin connections`) — rejected unless `allowDangerous: true` is set on that entry.
- **Unknown** — not in the allowlist; always rejected. The list widens on demand (open an issue), never the other way.

The typed dangerous toggles (`clrEnabled`, `crossDbOwnershipChaining`, `remoteAdminConnections`) follow the same rule via the top-level `spConfigure.allowDangerous`. The exact same classifier is re-applied by the privileged reconciler at the point it runs `sp_configure` — so the policy holds even if the admission webhook is disabled or bypassed.

### traceFlags

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `traceFlags.global` | array of int32 | No |   | Desired set of global trace flags. The operator reconciles to exactly this set at runtime (`DBCC TRACEON/TRACEOFF`). Flags in the list are enabled, and active flags not in the list are disabled. |
| `traceFlags.startup` | array of int32 | No |   | Startup trace flags. Written into the instance's `mssql.conf` `[traceflag]` section at StatefulSet build time — so they take effect only for instances that reference this configuration via `spec.configurationRef`, and require a pod restart. Not applied by the `SqlConfiguration` reconcile loop. |

### tempDb

If present, the operator grows tempdb up to the requested data-file count. It never removes existing files.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `tempDb.dataFiles` | int32 | No | `8` | Target number of tempdb data files. Files are added (using `initialSizeMb`/`autoGrowthMb`) until this count is reached. |
| `tempDb.initialSizeMb` | int32 | No | `1024` | Initial size for newly added data files. |
| `tempDb.autoGrowthMb` | int32 | No | `512` | Auto-growth increment for newly added data files. |
| `tempDb.logInitialSizeMb` | int32 | No | `256` | Initial size for the tempdb log file. |
| `tempDb.logAutoGrowthMb` | int32 | No | `256` | Auto-growth increment for the tempdb log file. |

### modelDatabase

Declared on the spec and present in the CRD, but **not applied by the current reconciler**.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `modelDatabase.recoveryModel` | string | No | `Simple` | Recovery model for the `model` database. |
| `modelDatabase.autoGrowthMb` | int32 | No | `256` | Auto-growth increment for the `model` database. |

### serverSettings

The default-path fields are written into the instance's `mssql.conf` at StatefulSet build time — for the instances that reference this configuration via `spec.configurationRef`, or that it selects by labels when it is the only one to — and take effect on the next pod build. They must stay under a volume the operator mounts, without control characters or `..` segments. The remaining fields are refused at admission: they are not applied.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `serverSettings.defaultDataPath` | string | No |   | `mssql.conf` `defaultdatadir`. |
| `serverSettings.defaultLogPath` | string | No |   | `mssql.conf` `defaultlogdir`. |
| `serverSettings.defaultBackupPath` | string | No |   | `mssql.conf` `defaultbackupdir`. |
| `serverSettings.errorLogSizeKb` | int32 | No |   | Not implemented: refused at admission. |
| `serverSettings.numErrorLogs` | int32 | No |   | Not implemented: refused at admission. |
| `serverSettings.authenticationMode` | string | No | `Mixed` | Only `Mixed` (the operator connects with SQL logins). |

### databaseMail

Only applied when `databaseMail.enabled` is `true`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `databaseMail.enabled` | bool | No | `false` | Enables Database Mail configuration on the instance. |
| `databaseMail.profiles` | array | No |   | Mail profiles. See [profiles](#databasemailprofiles). |
| `databaseMail.accounts` | array | No |   | Mail accounts. See [accounts](#databasemailaccounts). |

#### databaseMail.profiles[]

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `databaseMail.profiles[].name` | string | Yes | `""` | Profile name. |
| `databaseMail.profiles[].description` | string | No |   | Free-text description. |
| `databaseMail.profiles[].default` | bool | No | `false` | Marks the profile as the default. |

#### databaseMail.accounts[]

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `databaseMail.accounts[].name` | string | Yes | `""` | Account name. |
| `databaseMail.accounts[].description` | string | No |   | Free-text description. |
| `databaseMail.accounts[].emailAddress` | string | Yes | `""` | From address for the account. |
| `databaseMail.accounts[].displayName` | string | No |   | Display name shown to recipients. |
| `databaseMail.accounts[].replyTo` | string | No |   | Reply-to address. |
| `databaseMail.accounts[].profiles` | array of string | No |   | Names of profiles this account is attached to. |
| `databaseMail.accounts[].smtp` | object | Yes |   | SMTP settings. See [smtp](#databasemailaccountssmtp). |

#### databaseMail.accounts[].smtp

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `...smtp.server` | string | Yes | `""` | SMTP server hostname. |
| `...smtp.port` | int32 | No | `587` | SMTP port. |
| `...smtp.useSsl` | bool | No | `true` | Use SSL/TLS for the SMTP connection. |
| `...smtp.username` | string | No |   | SMTP username (when authentication is required). |
| `...smtp.credentialsSecretRef` | object | No |   | Reference to a Secret holding the SMTP password. |
| `...smtp.credentialsSecretRef.name` | string | Yes | `""` | Secret name (resolved in the resource's namespace). |
| `...smtp.credentialsSecretRef.key` | string | Yes | `""` | Key within the Secret. |

### agentOperators

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `agentOperators[].name` | string | Yes | `""` | Operator name. |
| `agentOperators[].enabled` | bool | No | `true` | Whether the operator is enabled. |
| `agentOperators[].emailAddress` | string | No |   | Notification email address. |
| `agentOperators[].pagerAddress` | string | No |   | Pager address. |
| `agentOperators[].weekdayPagerStartTime` | string | No |   | Weekday pager start time. |
| `agentOperators[].weekdayPagerEndTime` | string | No |   | Weekday pager end time. |
| `agentOperators[].saturdayPagerStartTime` | string | No |   | Saturday pager start time. |
| `agentOperators[].saturdayPagerEndTime` | string | No |   | Saturday pager end time. |

## Ownership recorded in SQL

The value each `sp_configure` option had before this configuration first changed it, and each trace
flag it turned on, are recorded in the `msdb.mssqlop` schema of the server, next to the
status. A configuration recreated by GitOps — same name, new UID, empty status — still puts those
values back and turns those flags off when its spec no longer sets them. Entries written before
these records existed are copied from the status to SQL on the next pass.

## Status

The status subresource is set by the controller on each reconcile. It is a slim, etcd-light status: a convergence signal plus O(1) roll-up scalars. The former per-instance `appliedTo[]` array (unbounded by the number of matched instances) has been removed.

| Field | Type | Description |
|-------|------|-------------|
| `phase` | string | High-level lifecycle phase: `Pending` (initial), `Applied`, or `Failed`/`PartiallyApplied`. |
| `observedGeneration` | integer (int64) | `metadata.generation` last reconciled by the operator. |
| `matchedInstances` | integer (int32) | Number of `SqlInstance` resources matched by the selector. |
| `appliedInstances` | integer (int32) | Matched instances the configuration was successfully applied to this pass. |
| `lastAppliedTime` | date-time | Timestamp of the last successful application pass (nullable). |
| `conditions` | array | Standard condition list. The controller maintains an `Applied` condition. |
| `conditions[].type` | string | Condition type. The controller emits `Applied`. |
| `conditions[].status` | string | `True` when applied to all targets, `False` when one or more failed. |
| `conditions[].reason` | string | `ConfigurationApplied` on full success, `PartiallyApplied` otherwise. |
| `conditions[].message` | string | Human-readable summary listing applied instances and any failures. |
| `conditions[].lastTransitionTime` | date-time | When the condition last changed (nullable). |

`kubectl get sqlconfiguration` shows: `PHASE` (`.status.phase`), `APPLIED` (`.status.appliedInstances`), `MATCHED` (`.status.matchedInstances`), and `AGE`.

**Live detail.** The per-instance list of exactly which instances were configured and when is no longer stored on the resource. Instead:

- The set of matched/applied instances is served live via the `view.mssql-operator.evariops.com` aggregated read API.
- Each successful or failed application is emitted as a Kubernetes Event (`kubectl describe sqlconfiguration <name>`).
- Counts over time are exposed as Prometheus metrics (`mssqloperator_targets_*`).

## Validation & behaviour

### Webhook validation

A validating webhook runs on create and update. It only inspects `spec.spConfigure`. If `spConfigure` is omitted, the resource is always accepted. All failures return HTTP 422:

- `maxServerMemoryMb` must be at least `128` (MB).
- `maxDegreeOfParallelism` must be between `0` and `32767` inclusive.
- `costThresholdForParallelism` must be non-negative.
- `minServerMemoryMb` must not exceed `maxServerMemoryMb` (only checked when both are set).
- **Dangerous-option opt-in.** Enabling any of `clrEnabled`, `crossDbOwnershipChaining`, `remoteAdminConnections` without `spConfigure.allowDangerous: true` is rejected. Each `advanced[]` entry is classified against the allowlist: *safe* passes, *dangerous* requires `allowDangerous: true` on that entry, *unknown* is always rejected. The reconciler re-applies the same classification at the point it runs `sp_configure`, so the policy still holds if the webhook is disabled.

### Reconcile behaviour

- **Targeting.** The controller lists all `SqlInstance` resources in the same namespace and selects an instance if **either** the instance's `spec.configurationRef.name` equals this resource's name, **or** every entry in `targetMatchLabels` matches a label on the instance metadata. An empty/absent `targetMatchLabels` does not match instances by label.
- **Connection.** For each targeted instance the operator resolves the instance's SA password from its `saPasswordSecretRef` and connects to `master` on the instance's pod (`<name>-0.<name>.<ns>.svc.cluster.local`) at the instance service port. Instances with no resolvable SA password are recorded as a failure and skipped.
- **Idempotent sp_configure.** Each `sp_configure` value (including `advanced[]` entries) is read first via the instance's current configuration and only changed when it differs, followed by a single `RECONFIGURE`.
- **Memory conflict.** If the targeted instance declares `spec.memory`, `maxServerMemoryMb` and `minServerMemoryMb` are skipped and a `MemoryConfigConflict` warning event is published.
- **Trace flags.** `traceFlags.global` is reconciled to the exact desired set. Missing flags are enabled, and extra active flags are disabled.
- **tempdb.** Data files are added until the count reaches `tempDb.dataFiles`, and files are never removed. When the instance has a dedicated tempdb volume (`spec.storage.tempDb`), existing tempdb files not already on `/var/opt/mssql/tempdb` are relocated there (which requires an instance restart to take effect) and new files target that path. Otherwise files use `/var/opt/mssql/data`.
- **Database Mail and Agent operators.** Database Mail is configured only when `databaseMail.enabled` is `true`, with SMTP credentials read from `credentialsSecretRef` in the resource's namespace. Agent operators are configured when `agentOperators` is present.
- **Applied via `mssql.conf` (not this reconcile loop).** `serverSettings.defaultDataPath`/`defaultLogPath`/`defaultBackupPath` and `traceFlags.startup` are baked into the instance's `mssql.conf` at StatefulSet build time. They apply to the instances that reference this configuration via `spec.configurationRef` (or that it alone selects by labels), and take effect on the next pod build/restart — they are not pushed by the `SqlConfiguration` reconcile loop.
- **Refused.** `serverSettings.errorLogSizeKb`/`numErrorLogs` and an `authenticationMode` other than `Mixed` are refused at admission: they are not applied.
- **Status & events.** After processing all targets the controller writes the `Applied` condition and publishes a `ConfigApplied` event summarising how many instances were configured. On deletion (or when an instance stops being targeted), the finalizer restores the `sp_configure` values recorded before the first change and turns off the trace flags the configuration enabled.

> Note: aside from tempdb file relocation onto a dedicated volume, applying a `SqlConfiguration` does not restart the instance. Some `sp_configure` options nonetheless require a restart to take effect per SQL Server semantics.

## Related

- [`SqlInstance`](./sqlinstance.md): the instances targeted by this configuration (via `targetMatchLabels` or `spec.configurationRef`).
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md): groups instances that may share configuration.
