# SqlAgentJob

A SQL Server Agent job (steps, schedules, notifications) kept identical on the instances and
availability groups it targets.

Guide: [Configuration, jobs and procedures](../guides/configuration.md).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAgentJob
metadata:
  name: nightly-stats
  namespace: sql
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

## Main fields

| Field | Default | What it does |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, same namespace. |
| `job.name` | required | Job name in msdb. |
| `job.owner` | `sa` | Owner login. With `sa` (or another privileged login) `allowDangerous: true` is required. |
| `job.allowDangerous` | `false` | Explicit opt-in for a sysadmin-owned job or for non-T-SQL steps (`CmdExec`, `PowerShell`, `SSIS`, `SSAS`). |
| `job.enabled` | `true` | Enabled state. |
| `job.steps[]` | required | `name`, `command`, `database` (`master`), `type` (`TSQL`), `onSuccess` / `onFailure`, retries. The last step quits with success by default. |
| `job.schedules[]` | | `frequencyType` (`Once`, `Daily`, `Weekly`, `Monthly`), `frequencyInterval`, `subdayType` / `subdayInterval` for repeats within the day, `startTime`, `startDate`. |
| `job.notifications` | | `onFailure` / `onCompletion`: an Agent operator (declared in a [`SqlConfiguration`](sqlconfiguration.md)) and `Email` or `Pager`. |

## On an availability group

msdb is not replicated, so the job is created on every replica, with a first step that stops it on
the replicas that are not the primary. It runs where the primary is, also after a failover.

## Status

```console
$ kubectl get sqlagentjob -n sql
NAME            PHASE        CONFIGURED   TARGETS   AGE
nightly-stats   Configured   1            1         4d
```

Run history, live from msdb: `kubectl get sqlagentjobruns -n sql`.

Deleting the resource deletes the job on every target.
