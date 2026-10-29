# Helm values

The values you are most likely to set. Every value is commented in
[`charts/mssql-operator/values.yaml`](../../charts/mssql-operator/values.yaml).

## Operator

| Value | Default | What it does |
|---|---|---|
| `replicaCount` | `2` | Operator replicas. One leads and reconciles; all serve the webhook and the view API. Keep 2 or more. |
| `image.repository` / `image.tag` | `ghcr.io/evariops/mssql-operator` / chart `appVersion` | Operator image. |
| `priorityClassName` | | Recommended: the operator serves cluster-wide APIs. |
| `resources` | 100m / 256Mi requested, 500m / 512Mi limit | |
| `logLevel` / `logFormat` | `Information` / `simple` | `json` for log pipelines. |
| `operator.workerCount` | `2` | Concurrent reconciles per resource kind. |
| `operator.maxConcurrentRestarts` | `1` | SQL Server restarts (pod template changes) allowed at once across the cluster; others wait (`RestartDeferred`). |
| `operator.engineUpgradePolicy` | `Approval` | A new default cumulative update brought by an operator upgrade waits for `approve-engine-upgrade` (`Automatic` applies it). |
| `operator.allowedImageRegistries` | `[]` | Registries SQL Server images may come from, besides `mcr.microsoft.com/`. `"*"` lifts the restriction. |
| `operator.safetyLevelMax` | `1` | Highest safety level an AG may run at. See [Automation](../guides/automation.md#safety-levels). |
| `operator.targetCircuitBreakers` | `Shadow` | Per-server circuit breakers: `Off`, `Shadow` (log only), `Enforce` (fail fast on a server seen down moments ago). |

## Admission webhook and view API

| Value | Default | What it does |
|---|---|---|
| `webhook.enabled` | `true` | Validates every resource at `kubectl apply`. |
| `webhook.failurePolicy` | `Fail` | `Fail`: no resource can be created or changed while the operator is unreachable. `Ignore`: availability over validation. |
| `webhook.certManager.enabled` | `false` | Let cert-manager issue the serving certificate. Recommended with GitOps tools that render with `helm template` (ArgoCD). Otherwise the chart generates a self-signed one and renews it 30 days before expiry. |
| `viewApi.enabled` | `true` | The [view API](view-api.md). |

## Breakout (dead node recovery)

| Value | Default | What it does |
|---|---|---|
| `breakout.enabled` | `true` | Handles SQL pods stuck on a lost node: moves them elsewhere after 10 minutes when every volume uses a trusted CSI driver (`FailoverBreakout__TrustedFencingCsiDrivers__0`, … on the Deployment; not exposed by the chart yet), otherwise reports `NeedsAttention`. See the [lost node runbook](../runbooks/lost-node.md). |
| `breakout.dedicated` | `true` | Runs it in its own Deployment and ServiceAccount (the only identity allowed to taint nodes and delete VolumeAttachments). |
| `breakout.fencingDriver` | `volumeattachment` | `none`: dry run, events only. |
| `breakout.terminatingThresholdSeconds` / `nodeReadyGracePeriodSeconds` | `30` / `60` | How long a pod must be stuck, and its node not ready, before acting. |
| `breakout.maxConcurrentFencing` | `3` | Upper bound of nodes fenced at once. |

## Connections, network, monitoring

| Value | Default | What it does |
|---|---|---|
| `sqlConnection.encrypt` | `Mandatory` | Operator → SQL Server TLS: `Optional`, `Mandatory`, `Strict`. |
| `sqlConnection.trustServerCertificate` | `true` | Set `false` with `caSecretName` to authenticate the SQL servers. See [Security](../guides/security.md). |
| `networkPolicy.enabled` | `true` | NetworkPolicy of the operator pods: egress to DNS, the API server, SQL Server and HTTPS only. |
| `networkPolicy.allowExternalHttps` | `true` | Egress 443 anywhere (GitHub sources, S3 backups). |
| `networkPolicy.webhookFromApiServerOnly` | `false` | Accept webhook calls from the API server addresses only. |
| `apiPriorityAndFairness.enabled` | `false` | FlowSchemas giving the operator's control writes their own priority level. |
| `prometheusRule.enabled` | `false` | The reference alerts (prometheus-operator). See [Observability](../guides/observability.md). |
