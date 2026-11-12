# Operator reference

This page lists the main Helm values, the view API resources and the metrics. All values are commented in [`charts/mssql-operator/values.yaml`](../../charts/mssql-operator/values.yaml).

## Helm values

### Operator

| Value | Default | Description |
|---|---|---|
| `replicaCount` | `2` | Operator replicas. One leads and reconciles; all serve the webhook and the view API. |
| `image.repository` / `image.tag` / `image.digest` | `ghcr.io/evariops/mssql-operator` / chart `appVersion` / empty | Operator image. A digest (`sha256:…`), when set, replaces the tag. |
| `priorityClassName` | | Recommended, since the operator serves cluster-wide APIs. Also applies to the breakout pods. |
| `priorityClass.create` / `priorityClass.value` | `false` / `1000000` | Creates a PriorityClass named after the release, used when `priorityClassName` is empty. |
| `topologySpreadConstraints` | zone spread | Empty: the replicas are spread over `topology.kubernetes.io/zone`, without ever staying Pending (`ScheduleAnyway`). A list replaces that default. |
| `resources` | 100m / 256Mi requested, 500m / 512Mi limit | |
| `logLevel` / `logFormat` | `Information` / `simple` | `json` for log pipelines. |
| `operator.workerCount` | `2` | Concurrent reconciles per resource kind. |
| `operator.maxConcurrentRestarts` | `1` | SQL Server restarts allowed at the same time across the cluster; others report `RestartDeferred`. |
| `operator.engineUpgradePolicy` | `Approval` | A new default CU brought by an operator upgrade waits for `approve-engine-upgrade`. `Automatic` applies it. |
| `operator.allowedImageRegistries` | `[]` | Registries allowed for SQL Server images in addition to `mcr.microsoft.com/`. `"*"` removes the restriction. |
| `operator.safetyLevelMax` | `1` | Highest safety level an AG can run at. |
| `operator.plannedFailoverLsnCheck` | `Shadow` | After the demotion of the primary, a planned failover checks that the target holds its whole log. `Shadow` publishes `PlannedFailoverLsnCheck` when it does not verify and goes on; `Enforce` rolls the failover back. |
| `operator.nodeMaintenance.mode` | `Shadow` | Node maintenance awareness: `Off`, `Shadow` (signals published, nothing acted on) or `Enforce`. See [Node maintenance](../guide/node-maintenance.md). |
| `operator.nodeMaintenance.disruptionTaints` | `[]` | Taint keys that announce a node removal, on top of the built-in ones. |
| `operator.nodeMaintenance.evictionDeadlineSeconds.node` | `210` | Longest delay of an eviction asked under a node identity (`system:node:*`, the drain of `talosctl upgrade`). |
| `operator.nodeMaintenance.evictionDeadlineSeconds.other` | `600` | Longest delay of an eviction asked by any other requester. |
| `operator.nodeMaintenance.evictionWebhook.namespaceSelector` | `{}` | Namespaces whose evictions go through the `pods/eviction` webhook. |
| `operator.targetCircuitBreakers` | `Shadow` | `Off`, `Shadow` (log only) or `Enforce` (fail fast on a server seen down recently). |
| `operator.automation.maxMode` | `Apply` | Ceiling of every resource's `spec.automation.mode`. `Plan` or `Observe` holds every disruptive and destructive action in the cluster. See [Automation modes](../guide/operating.md#automation-mode). |
| `operator.automation.maxDestructivePerHour` | `3` | Destructive actions pre-authorized by `spec.automation.preAuthorized` that the whole operator may run in an hour. `0` makes each of them wait for approval. |

### Admission webhook and view API

| Value | Default | Description |
|---|---|---|
| `webhook.enabled` | `true` | Validates every resource at `kubectl apply`. |
| `webhook.failurePolicy` | `Fail` | `Fail`: nothing can be created or changed while the operator is unreachable. `Ignore`: availability over validation. |
| `webhook.certManager.enabled` | `false` | Uses cert-manager for the serving certificate, recommended with GitOps tools that render with `helm template`. Otherwise the chart generates a certificate and renews it 30 days before expiry. |
| `viewApi.enabled` | `true` | Enables the view API. Requires `webhook.enabled`. |

### Breakout

| Value | Default | Description |
|---|---|---|
| `breakout.enabled` | `true` | Relocates SQL Server pods stuck on lost nodes when their storage is trusted, and raises `NeedsAttention` otherwise. |
| `breakout.dedicated` | `true` | Runs it in its own Deployment and ServiceAccount, with a PodDisruptionBudget when it has more than one replica. |
| `breakout.topologySpreadConstraints` | zone spread | Same as `topologySpreadConstraints`, for the breakout replicas. |
| `breakout.fencingDriver` | `volumeattachment` | `none` reports without acting, and also removes the operator's permission to delete VolumeAttachments. |
| `breakout.terminatingThresholdSeconds` / `nodeReadyGracePeriodSeconds` | `30` / `60` | Minimum time a pod must be stuck, and its node NotReady, before the breakout considers it. |
| `breakout.maxConcurrentFencing` | `3` | Maximum number of nodes handled at the same time. |
| `breakout.trustedFencingCsiDrivers` | `[]` | CSI drivers whose detach removes a lost node's access to the data. A pod stuck on a lost node is relocated automatically only when all its volumes use one of them. Empty: every lost node needs the [manual procedure](../failure-modes.md#a-node-is-lost). When set, the fencing of an unreachable AG primary before a failover also accepts a detachment as proof only for these drivers. |
| `breakout.relocationDelaySeconds` | `600` | Time the node must stay NotReady, its Lease not renewed, before that relocation. |

The breakout settings go to the breakout Deployment when `breakout.dedicated` is `true`, and to the operator Deployment otherwise.

### Connections, network, monitoring

| Value | Default | Description |
|---|---|---|
| `sqlConnection.encrypt` | `Mandatory` | TLS from the operator to SQL Server: `Optional`, `Mandatory` or `Strict`. |
| `sqlConnection.trustServerCertificate` | `true` | `false`, with `caSecretName`, verifies the server certificates. |
| `networkPolicy.enabled` | `true` | NetworkPolicy of the operator pods. |
| `networkPolicy.allowExternalHttps` | `true` | Egress on port 443 to any destination (GitHub sources, S3 backups). |
| `networkPolicy.webhookFromApiServerOnly` | `false` | Accepts webhook calls from the API server addresses only. |
| `apiPriorityAndFairness.enabled` | `false` | FlowSchemas giving the operator's control writes their own priority level. |
| `prometheusRule.enabled` | `false` | Reference alerting rules for prometheus-operator. |

## View API

The view API serves live, read-only data that is not stored in the status, read from SQL Server on request. It is a Kubernetes aggregated API (`view.mssql-operator.evariops.com/v1alpha1`), so `kubectl get` works with the usual RBAC.

| Resource | Content | Related resource |
|---|---|---|
| `agreplicastates` | role, synchronization health, connection and failover readiness of each replica | `SqlAvailabilityGroup` |
| `logchains` | full, differential and log backups recorded on each replica (latest 50), with the operator's own backups flagged | `SqlAvailabilityGroupDatabase` |
| `tdedatabasestates` | encryption state of each selected database | `SqlTdeCertificate` |
| `storedproceduredeployments` | deployment state of each procedure in each database | `SqlStoredProcedure` |
| `sqlusertargets` | the login and database users on each target: existence, enabled state, roles | `SqlUser` |
| `sqlagentjobruns` | last run, last status and next run of the job on each target | `SqlAgentJob` |
| `operatorplans` | actions executed, withheld or awaiting approval, with their ids | every resource |

```bash
kubectl get agreplicastates -n sql
kubectl get operatorplans -n sql -o wide     # -o wide adds lower-priority columns
kubectl get logchains -n sql -o yaml         # full items
```

Watches are not supported. Data is computed on request and cached for 10 seconds, and an unreachable target is skipped without failing the list.

The chart creates the ClusterRole `<fullname>-view-reader` (`mssql-operator-view-reader` for a release named `mssql-operator`) with `get` and `list` on these resources. It is meant to be bound per namespace:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata: { name: sql-view-readers, namespace: sql }
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: mssql-operator-view-reader
subjects:
  - kind: Group
    name: sql-admins
    apiGroup: rbac.authorization.k8s.io
```

While no operator pod answers, the API is unavailable, which also delays namespace deletions across the cluster.

## Metrics

Metrics are OpenTelemetry instruments exported in Prometheus format on port 8080 (`/metrics`) of the operator pods, and defined in [`OperatorMetrics`](../../src/MsSqlOperator/Services/Metrics/OperatorMetrics.cs). In Prometheus, dots become underscores and the unit is appended: `mssqloperator.needs_attention` is exported as `mssqloperator_needs_attention`, and `mssqloperator.ag.last_health_check_timestamp` as `mssqloperator_ag_last_health_check_timestamp_seconds`. All instrument names below have the `mssqloperator.` prefix.

| Area | Instruments |
|---|---|
| Human decisions | `needs_attention` (labels kind, namespace, name) |
| Node maintenance | `maintenance.in_progress` (labels kind, namespace, name): 1 while a pod of the resource carries a maintenance signal |
| Planned failovers | `ag.failover.write_pause` (histogram, seconds; labels namespace, ag, reason `PlannedFailover`, `NodeMaintenance`…): how long a planned failover routed no writer, from the old primary de-routed to the new one routed |
| Eviction delays | `eviction.delay` (histogram, seconds; labels namespace, ag, outcome `verdict`, `deadline` or `release`): how long the eviction webhook delayed a replica |
| Availability groups | `ag.replicas.total`, `ag.replicas.synchronized`, `ag.replicas.disconnected`, `ag.replicas.resolving`, `ag.synced_secondaries`, `ag.log_send_queue.bytes`, `ag.redo_queue.bytes`, `ag.failover.total`, `ag.health_check.duration`, `ag.last_health_check_timestamp`, `ag.rolling_update.in_progress`, `operation.in_progress`, `ag.heartbeat.duration`, `ag.heartbeat.failures.total`, `watchdog.last_run_timestamp` |
| Decisions | `actuation.decisions.total` (by risk and decision), `operator_log_backups.total` |
| Targets | `targets.total`, `targets.ready`, `targets.failed`, `instances.count`, `tde.databases.encrypted`, `tde.databases.pending`, `tde.databases.failed`, `storedproc.deployed`, `storedproc.desired` |
| Runtime | `is_leader`, `reconciliation.total`, `reconciliation.errors.total`, `reconciliation.duration`, `reconcile.last_success_timestamp`, `workqueue.depth`, `workqueue.oldest_age`, `workqueue.inflight_oldest`, `owned_watch.enqueues.total`, `owned_watch.last_event_age_seconds`, `event_source.lag`, `event_source.lost.total`, `event_source.last_event_timestamp`, `actor.reevaluations.total`, `writes.dropped.total` |

The reference alerts are listed in [Operations](../guide/operating.md#alerts), and a Grafana dashboard is provided in [`docs/observability/grafana-dashboard.json`](../observability/grafana-dashboard.json).
