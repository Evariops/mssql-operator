# Configuration, jobs and procedures

Three resources manage what lives inside SQL Server besides databases and logins:

| Resource | Manages |
|---|---|
| [`SqlConfiguration`](../reference/sqlconfiguration.md) | `sp_configure` options, trace flags, tempdb files, Database Mail, Agent operators |
| [`SqlAgentJob`](../reference/sqlagentjob.md) | SQL Server Agent jobs |
| [`SqlStoredProcedure`](../reference/sqlstoredprocedure.md) | stored procedures, inline or from GitHub |

## Targeting

`SqlAgentJob` and `SqlStoredProcedure` (like `SqlUser` and `SqlTdeCertificate`) choose their servers
with `spec.target`:

```yaml
spec:
  target:
    instances: [reporting]          # SqlInstance names
    availabilityGroups: [orders]    # SqlAvailabilityGroup names
    matchLabels: { tier: oltp }     # or labels on those resources
```

Targets are always in the resource's own namespace. A `SqlConfiguration` uses `targetMatchLabels`, or
is named by the server's `configurationRef`.

On an availability group, server-level objects that SQL Server does not replicate (configuration,
Agent jobs, logins) are applied to every replica; objects inside a database (procedures, database
users) are applied on the primary and replicated by the AG.

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
```

- Memory is driven by the server's `memory.total`; leave `maxServerMemoryMb` unset for those servers.
- Options that open code execution (`xp_cmdshell`, `clr enabled`, …) require `allowDangerous: true`.
- Removing an option from the spec, or deleting the configuration, restores the value it had before.
- Options that only apply after a restart are reported with `PendingRestart`; the operator does not
  restart SQL Server for them.

## Jobs

A job owned by `sa` (the default owner) runs its steps as sysadmin; the webhook asks you to confirm
with `job.allowDangerous: true`, or to set a non-privileged `job.owner`. Same for `CmdExec` and
`PowerShell` steps. On an AG the job exists on every replica and only runs on the primary.

See the run history: `kubectl get sqlagentjobruns -n sql`.

## Stored procedures

Procedures are deployed with `CREATE OR ALTER`, all changed procedures of a database in one
transaction, through a login that is only `db_ddladmin` on the targeted databases. From GitHub, pin a
`ref.tag` or `ref.sha`, or follow releases (`sync.followLatestRelease`). Only changed procedures are
redeployed.

See what is deployed where: `kubectl get storedproceduredeployments -n sql`.
