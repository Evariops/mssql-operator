# `SqlAgentJob`

> Declaratively manages SQL Server Agent jobs (their steps, schedules, and notifications) on the SQL Server instances selected by a target.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlAgentJob` · scope: Namespaced · plural: `sqlagentjobs`

## Minimal example

```yaml
apiVersion: mssql.operator/v1alpha1
kind: SqlAgentJob
metadata:
  name: nightly-maintenance
  namespace: sql
spec:
  target:
    instances:
      - prod-sql
  job:
    name: Nightly Maintenance
    steps:
      - name: Update Statistics
        command: EXEC sp_updatestats;
```

## Overview

A `SqlAgentJob` provisions and continuously reconciles a single SQL Server Agent job across one or more targeted instances and availability groups. It defines the job's metadata (name, owner, category), its ordered execution steps, recurring schedules, and operator notifications. The operator creates the job if missing, otherwise rewrites its steps and schedules to match the spec, and keeps the desired enabled state. For an Availability Group target, the job is provisioned only on the current primary replica, because SQL Server Agent jobs run on the primary.

## Spec

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `target` | `TargetSelector` | Yes | `{}` | Selects the SQL Server instances / availability groups the job is applied to. See [Target selector](#target-selector). |
| `job` | `JobDefinitionSpec` | Yes | `null` | The Agent job definition. Although nullable in the schema, the validating webhook rejects a spec without it. See [Job definition](#job-definition). |

### Target selector

`spec.target`. At least one targeted selector must be set (see [Validation & behaviour](#validation--behaviour)).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `target.matchLabels` | `map[string]string` | No | `null` | Label selector matching `SqlInstance` / `SqlAvailabilityGroup` resources. |
| `target.instances` | `string[]` | No | `null` | Explicit `SqlInstance` names to target. |
| `target.availabilityGroups` | `string[]` | No | `null` | Explicit `SqlAvailabilityGroup` names to target (job is provisioned on the primary replica). |

### Job definition

`spec.job` (`JobDefinitionSpec`)

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `job.name` | `string` | Yes | `""` | SQL Agent job name. Must be non-empty. |
| `job.description` | `string` | No | `null` | Free-text job description. |
| `job.enabled` | `bool` | No | `true` | Desired enabled state of the job after reconciliation. |
| `job.category` | `string` | No | `Uncategorized (Local)` | SQL Agent job category. Falls back to `Uncategorized (Local)` when unset. |
| `job.owner` | `string` | No | `sa` | Owner login for the job. Falls back to `sa` when unset. |
| `job.steps` | `JobStepSpec[]` | Yes | `null` | Ordered execution steps. Must contain at least one step. See [Steps](#steps). |
| `job.schedules` | `JobScheduleSpec[]` | No | `null` | Recurring schedules for the job. See [Schedules](#schedules). |
| `job.notifications` | `JobNotificationSpec` | No | `null` | Operator notification settings. See [Notifications](#notifications). |

### Steps

`spec.job.steps[]` (`JobStepSpec`)

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | `string` | Yes | `""` | Step name. Must be non-empty. |
| `order` | `int` | No | `1` | Step id and execution order. Must be unique within the job (used as `step_id`, with steps created in ascending order). |
| `type` | `string` | No | `TSQL` | Subsystem. One of `TSQL`, `CmdExec`, `PowerShell`, `SSIS`, `SSAS` (case-insensitive). |
| `database` | `string` | No | `master` | Database context for the step. Defaults to `master` when unset. |
| `command` | `string` | Yes | `""` | Command / T-SQL text to execute. Must be non-empty. |
| `onSuccess` | `string` | No | `GoToNextStep` | Action on success. One of `GoToNextStep`, `QuitWithSuccess`, `QuitWithFailure`, `GoToStep` (case-insensitive). |
| `onFailure` | `string` | No | `QuitWithFailure` | Action on failure. Same allowed values as `onSuccess`. |
| `retryAttempts` | `int` | No | `0` | Number of retry attempts for the step. |
| `retryIntervalMinutes` | `int` | No | `5` | Minutes between retries. |

### Schedules

`spec.job.schedules[]` (`JobScheduleSpec`)

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | `string` | No | `""` | Schedule name. |
| `enabled` | `bool` | No | `true` | Whether the schedule is enabled. |
| `frequencyType` | `string` | No | `Daily` | Recurrence type. One of `Once`, `Daily`, `Weekly`, `Monthly` (case-insensitive). |
| `frequencyInterval` | `int` | No | `1` | Interval value, interpreted per `frequencyType` (`freq_interval`). |
| `startTime` | `string` | No | `null` | Active start time, parsed as a time-of-day (e.g. `02:30:00`). Unparseable values default to midnight. |
| `startDate` | `string` | No | `null` | Active start date. Present in the spec/CRD but not currently applied to the underlying schedule. |

### Notifications

`spec.job.notifications` (`JobNotificationSpec`)

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `onFailure` | `NotificationAction` | No | `null` | Notification to raise when the job fails. |
| `onCompletion` | `NotificationAction` | No | `null` | Notification to raise when the job completes. |

`NotificationAction`

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `operator` | `string` | No | `null` | Target SQL Agent operator name. |
| `notifyMethod` | `string` | No | `null` | Delivery method. `Email` and `Pager` are recognised (case-insensitive). Other values are ignored. |
| `writeToEventLog` | `bool` | No | `false` | Whether to also write a notification to the Windows event log. |

## Status

| Field | Type | Description |
|-------|------|-------------|
| `phase` | `string` | High-level lifecycle phase: `Pending` (initial), `Running`/`Configured`, or `Failed`/`PartiallyConfigured`. |
| `observedGeneration` | `int64` | `metadata.generation` last reconciled by the operator. |
| `totalTargets` | `int32` | Targets (instances/AGs) matched by the selector. |
| `configuredTargets` | `int32` | Targets the job was successfully configured on this pass. |
| `conditions` | `ConditionStatus[]` | Standard conditions. Known types: `Ready` (`JobConfigured` / `PartialError`) and `NoTargetsFound` (`NoMatch`). |

This is a slim, etcd-light status: a convergence signal plus O(1) roll-up scalars. The former per-target `targetStatuses[]` array — which held live `msdb` run data (`jobId`, `lastRunDate`, `lastRunStatus`, `nextRunDate`, `lastError`) that changes constantly — has been removed.

`kubectl get sqlagentjob` shows: `PHASE` (`.status.phase`), `CONFIGURED` (`.status.configuredTargets`), `TARGETS` (`.status.totalTargets`), and `AGE`.

**Live detail.** Per-target run state (last/next run, status, job id) is no longer stored on the resource. Instead:

- Live run state is served via the `view.mssql.operator` aggregated read API — `kubectl get sqlagentjobruns`, queried live from `msdb` on demand.
- Configuration occurrences (job created/updated, target failures) are emitted as Kubernetes Events (`kubectl describe sqlagentjob <name>`).
- Counts over time are exposed as Prometheus metrics (`mssqloperator_targets_*`).

`ConditionStatus`

| Field | Type | Description |
|-------|------|-------------|
| `type` | `string` | Condition type (e.g. `Ready`, `NoTargetsFound`). |
| `status` | `string` | `True` / `False`. |
| `reason` | `string` | Short machine-readable reason. |
| `message` | `string` | Human-readable detail. |
| `lastTransitionTime` | `datetime` | When the condition last changed. |

## Validation & behaviour

### Webhook validation

The validating webhook (`SqlAgentJobValidator`) runs on create and update and rejects (HTTP 422) when:

- The target selector is empty (must specify `matchLabels`, `instances`, or `availabilityGroups`).
- `spec.job` is missing.
- `job.name` is empty or whitespace.
- `job.steps` is null or empty (at least one step is required).
- Any step has an empty `name` or empty `command`.
- Two steps share the same `order` (duplicate step order).
- A step's `type` is not one of `TSQL`, `CmdExec`, `PowerShell`, `SSIS`, `SSAS`.
- A step's `onSuccess` or `onFailure` is not one of `GoToNextStep`, `QuitWithSuccess`, `QuitWithFailure`, `GoToStep`.
- A schedule's `frequencyType` is not one of `Once`, `Daily`, `Weekly`, `Monthly`.

All enum checks are case-insensitive.

### Reconcile behaviour

On each reconcile the operator resolves the target selector to a set of instances / availability groups, then for each target:

1. Resolves the connection. For a `SqlAvailabilityGroup` it locates the primary replica (SQL Agent jobs run only on the primary). For a `SqlInstance` it connects to `master`.
2. Looks up the job by name. If it does not exist, the job is created (`sp_add_job` plus `sp_add_jobserver`). Otherwise it is first disabled to prevent it from running with a partial definition during the update.
3. Deletes all existing steps and recreates them from `spec.job.steps`, ordered by `order`.
4. Deletes all existing schedules and recreates them from `spec.job.schedules`.
5. Re-applies the desired `enabled` state via `sp_update_job`.
6. Applies notifications (when `spec.job.notifications` is set) by mapping `Email` / `Pager` methods and `writeToEventLog` to SQL Agent notify levels.

The status records the roll-up scalars (`phase`, `totalTargets`, `configuredTargets`) and conditions. If no targets match the selector, a `NoTargetsFound` condition is set and reconciliation succeeds without error. A failure on one target is surfaced as a `Ready=False` / `PartialError` condition and counted in (`totalTargets` − `configuredTargets`), while other targets continue to reconcile; the per-target error is emitted as a Kubernetes Event. Live last-run / next-run information is no longer written into status — it is served on demand from `msdb` via the `view.mssql.operator` API (`kubectl get sqlagentjobruns`).

### Delete handling

A finalizer (`SqlAgentJobFinalizer`) runs on deletion. It re-resolves the targets and drops the job on each one via `sp_delete_job` (with `@delete_unused_schedule = 1`). If deletion fails on a target, finalization fails and is retried, unless the resource carries the annotation `mssql.operator/force-finalize: "true"`, in which case the error is logged and finalization proceeds.

## More examples

A multi-step job with a label-selected target, a recurring schedule, and failure notifications:

```yaml
apiVersion: mssql.operator/v1alpha1
kind: SqlAgentJob
metadata:
  name: nightly-maintenance
  namespace: sql
spec:
  target:
    matchLabels:
      tier: production
  job:
    name: Nightly Maintenance
    description: Rebuild indexes and update statistics
    enabled: true
    category: Database Maintenance
    owner: sa
    steps:
      - name: Rebuild Indexes
        order: 1
        type: TSQL
        database: AppDb
        command: EXEC dbo.usp_RebuildIndexes;
        onSuccess: GoToNextStep
        onFailure: QuitWithFailure
        retryAttempts: 2
        retryIntervalMinutes: 5
      - name: Update Statistics
        order: 2
        type: TSQL
        database: AppDb
        command: EXEC sp_updatestats;
        onSuccess: QuitWithSuccess
        onFailure: QuitWithFailure
    schedules:
      - name: Every night at 02:30
        enabled: true
        frequencyType: Daily
        frequencyInterval: 1
        startTime: "02:30:00"
    notifications:
      onFailure:
        operator: DBA Team
        notifyMethod: Email
        writeToEventLog: true
```

## Related

- [`SqlInstance`](./sqlinstance.md)
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md)
