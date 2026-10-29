# Observability

## The one signal to alert on: NeedsAttention

Every resource has a `NeedsAttention` condition. It is `True` only when the operator needs a human
decision, and its reason and message say which one:

| Resource | Typical reasons |
|---|---|
| `SqlAvailabilityGroup` | `SplitBrain` (writes drained), `NoLosslessFailoverTarget` / `DataLossFailoverBlocked` (no primary and no automatic way out), `RollingUpdateFailed`, `VolumeFull` |
| `SqlAvailabilityGroupDatabase` | `TransactionLogFull`, `ReseedBlocked`, `ManualRejoinRequired` |
| `SqlInstance` | machine key replaced or missing, `sa` password refused |

It is exported as the gauge `mssqloperator_needs_attention{kind,namespace,name}`.

```bash
kubectl get sqlavailabilitygroup,sagdb,sqlinstance -A \
  -o custom-columns='KIND:.kind,NS:.metadata.namespace,NAME:.metadata.name,ATTENTION:.status.conditions[?(@.type=="NeedsAttention")].message'
```

## Alerts

`prometheusRule.enabled=true` installs the reference rules (prometheus-operator):

| Alert | Severity | Meaning |
|---|---|---|
| `MsSqlOperatorNeedsAttention` | critical | a resource needs a decision (the only one that should page) |
| `MsSqlOperatorAgNotHealthChecked` | critical | an AG was not checked for 2 minutes: a dead primary would go unnoticed |
| `MsSqlOperatorNoLeader` | critical | no operator replica leads: nothing is reconciled |
| `MsSqlOperatorWatchdogSilent` | critical | a quorum watchdog job stopped running (safety level 2) |
| `MsSqlOperatorReplicaResolving` | warning | a replica stays `RESOLVING` while another one is primary |
| `MsSqlOperatorActionAwaitingApproval` | warning | a destructive action waits for an approval |
| `MsSqlOperatorWorkQueueStuck`, `MsSqlOperatorOperationStuck` | warning | the operator is saturated, or a failover / rolling update lasts more than 30 min |
| `MsSqlOperatorWritesDropped`, `MsSqlOperatorWatchSilent`, `MsSqlOperatorWatchLosses`, `MsSqlOperatorAgHeartbeatFailing` | warning | the operator itself is degraded |

## Events and conditions

The operator explains what it does with Kubernetes events (`kubectl describe` or
`kubectl get events -n sql --field-selector involvedObject.name=orders`): failovers
(`FailoverCompleted`, `ReplicaFenced`, `ResurrectedPrimaryDemoted`), re-seeds (`ReplicaReseeded`),
backups (`LogBackedUp`), decisions of the automation gate (`ActionPlanned`, `ActionRequiresApproval`),
durability (`RequiredSyncLowered`, `DurabilityFloorHoldingWrites`).

Live details that are not stored in the status are in the [view API](../reference/view-api.md):
`kubectl get agreplicastates`, `logchains`, `operatorplans`, …

## Metrics

Prometheus metrics are served on port 8080 (`/metrics`) of the operator pods. The main ones:

| Metric | Meaning |
|---|---|
| `mssqloperator_needs_attention` | 1 while a resource needs a decision |
| `mssqloperator_ag_replicas_total`, `_synchronized`, `_disconnected`, `_resolving` | replica counts per AG |
| `mssqloperator_ag_log_send_queue_bytes`, `mssqloperator_ag_redo_queue_bytes` | replication lag per replica |
| `mssqloperator_ag_failover_total` | failovers by type |
| `mssqloperator_ag_last_health_check_timestamp_seconds` | last health check of each AG |
| `mssqloperator_actuation_decisions_total{risk,decision}` | decisions of the automation gate |
| `mssqloperator_operator_log_backups_total` | log backups taken by the operator |
| `mssqloperator_is_leader` | 1 on the leading replica |
| `mssqloperator_workqueue_depth`, `mssqloperator_workqueue_oldest_age_seconds` | reconcile backlog |

## Dashboard, traces and logs

- Grafana dashboard: [`grafana-dashboard.json`](../observability/grafana-dashboard.json), to import.
- Traces: one trace per operation (failover, fencing, re-seed, TDE rotation, deployment), exported over
  OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set on the operator.
- Logs: one line per entry (`logFormat: simple`) or JSON (`json`), each with the resource kind, name
  and reconcile id.
- Health: `/healthz` (liveness: fails when the operator stops processing its queue) and `/readyz` on
  port 8080.
