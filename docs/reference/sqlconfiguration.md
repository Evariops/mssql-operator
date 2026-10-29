# SqlConfiguration

Server-wide settings applied to the instances and availability groups it targets: `sp_configure`
options, trace flags, tempdb files, Database Mail and SQL Agent operators.

Guide: [Configuration, jobs and procedures](../guides/configuration.md).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlConfiguration
metadata:
  name: oltp-defaults
  namespace: sql
spec:
  targetMatchLabels:
    tier: oltp                      # SqlInstance / SqlAvailabilityGroup carrying this label
  spConfigure:
    maxDegreeOfParallelism: 4
    costThresholdForParallelism: 50
    optimizeForAdHocWorkloads: true
  traceFlags:
    global: [3226]
  tempDb:
    dataFiles: 8
```

## Targets

A configuration applies to:

- every `SqlInstance` whose `spec.configurationRef.name` names it, and every `SqlAvailabilityGroup`
  whose `spec.instanceTemplate.configurationRef.name` names it (all replicas);
- every instance or AG in the same namespace whose labels match `targetMatchLabels`.

## Main fields

| Field | What it does |
|---|---|
| `spConfigure.<option>` | Typed options: `maxDegreeOfParallelism`, `costThresholdForParallelism`, `optimizeForAdHocWorkloads`, `backupCompressionDefault`, `containedDatabaseAuthentication`, … Unset options are left alone. Memory is not set here when the instance declares `memory`. |
| `spConfigure.advanced[]` | Any other `sp_configure` option by `name` and `value`, from an allow-list. Options that open code execution (`xp_cmdshell`, `clr enabled`, …) also need `allowDangerous: true`. |
| `traceFlags.global` | Exact set of global trace flags: missing ones are turned on, extra ones off. |
| `traceFlags.startup` | Startup trace flags, written to `mssql.conf`; applied at the next pod restart. |
| `tempDb` | Number and size of tempdb data files. Files are added, never removed. |
| `serverSettings.defaultDataPath` / `defaultLogPath` / `defaultBackupPath` | Written to `mssql.conf`; applied at the next pod restart. |
| `databaseMail` | Profiles and SMTP accounts (password from a Secret). Needs an egress rule to the relay when a network policy is on. |
| `agentOperators[]` | SQL Agent operators (who gets notified by jobs). |

## Ownership

Before changing an option, the operator records its previous value in `msdb.mssqlop` on that server.
When the configuration is deleted, or stops targeting a server, the previous values are put back and
the trace flags it turned on are turned off. This also works when GitOps recreates the resource.

## Status

```console
$ kubectl get sqlconfiguration -n sql
NAME            PHASE     APPLIED   MATCHED   AGE
oltp-defaults   Applied   3         3         1d
```

`status.phase`: `Pending`, `Applied`, `PartiallyApplied`, `Failed`. Some options take effect only
after a restart: the condition `PendingRestart` says so. Applying a configuration never restarts
SQL Server by itself.
