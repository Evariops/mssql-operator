# Operations

This guide covers the operator itself: installation and upgrades, SQL Server builds, automation modes and approvals, safety levels, monitoring and production hardening.

## Installation

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system --create-namespace
```

The chart installs the CRDs, two operator replicas (one leads), the admission webhook with a generated certificate, the [view API](../reference/operator.md#view-api) and the breakout controller. Release notes are on [GitHub](https://github.com/Evariops/mssql-operator/releases). The main values are listed in the [reference](../reference/operator.md#helm-values), and all of them are commented in [`values.yaml`](../../charts/mssql-operator/values.yaml).

## Upgrading the operator

```bash
helm upgrade mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> --namespace mssql-operator-system
```

The CRDs are chart templates, so `helm upgrade` updates them, and `helm uninstall` leaves them in place together with the resources.

A release can change the pod template of the SQL Server StatefulSets, and a template change restarts SQL Server. AGs roll one replica at a time, with one planned failover each, and at most `operator.maxConcurrentRestarts` resources (1 by default) restart at the same time across the cluster. The release notes say whether a release changes the template. To hold back a resource during an upgrade, annotate it with `mssql-operator.evariops.com/reconcile-paused=true` and remove the annotation when it may roll. A `SqlInstance` can also use `updateStrategy.type: OnDelete`, so that it restarts only when its pod is deleted.

### Upgrading from 0.1.5

- The `preStop` hook changes, so every SQL Server pod restarts once. The hook now checkpoints the user databases, and on an AG replica it first waits for the primary role to be handed over ([Stops without an eviction](node-maintenance.md#stops-without-an-eviction)).
- The chart adds a validating webhook on `pods/eviction` (`failurePolicy: Ignore`, 3 s timeout). With `operator.nodeMaintenance.mode: Shadow`, the default, it records the eviction requests of SQL Server pods and allows them all. `Enforce` delays them, as described in [Node maintenance](node-maintenance.md). `Off` removes the webhook.
- `podDisruptionBudget.enabled` on a `SqlInstance` is ignored with a warning, and the operator deletes the PDB it created. That PDB blocked every drain of the node; a drain now stops the instance.
- A Standard AG no longer sets `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT`, which SQL Server refuses on a basic availability group. A Standard AG left in `Initializing` by this error completes its initialization.
- SQL Server 2025 servers that set no `cumulativeUpdate` report `EngineUpgradePending` for CU10 until `approve-engine-upgrade: CU10` is set, under `operator.engineUpgradePolicy: Approval`.
- An Enterprise AG created with `synchronousCount: 3` and no `durability` runs `Quorum`. Existing AGs keep their durability.
- After the demotion of the primary, a planned failover compares the last hardened LSN of each database on both replicas. With `operator.plannedFailoverLsnCheck: Shadow`, the default, a mismatch only publishes `PlannedFailoverLsnCheck`.

### Upgrading from 0.1.4 or earlier

- The StatefulSets of the AGs are recreated with `podManagementPolicy: Parallel`, keeping their pods and volumes. Every SQL Server pod then restarts once (new in-memory key volume, new DNS settings), which is one rolling update and one planned failover per AG.
- A `SqlControlRecord` is created for each AG and each instance.
- Existing AGs keep `Legacy` durability. Moving to `Quorum` is described in [Availability groups](availability-groups.md#durability).
- A `SqlAvailabilityGroupDatabase` without `logBackup.mode` reports `LogBackupModeDeprecated`. Set `Local` or `Disabled`.
- Safety level 0 remains the default. Once level 1 has been observed in shadow mode, annotate the AGs with `safety-level: "1"`.

### Earlier breaking changes

- The API group changed from `mssql.operator` to `mssql-operator.evariops.com`, without conversion. Export the resources, rewrite `apiVersion`, delete the old resources and CRDs, install the chart and re-apply.
- `target.namespaces` was removed. Resources only target servers in their own namespace.
- `databaseFilter.pattern` is anchored (`^…$`). A pattern that relied on substring matching may match fewer databases; prefix it with `.*` if needed.
- CRDs installed by an old chart or by `kubectl apply` must be labelled for Helm once before upgrading:

  ```bash
  for crd in $(kubectl get crd -o name | grep 'mssql-operator.evariops.com'); do
    kubectl annotate "$crd" meta.helm.sh/release-name=mssql-operator --overwrite
    kubectl annotate "$crd" meta.helm.sh/release-namespace=mssql-operator-system --overwrite
    kubectl label "$crd" app.kubernetes.io/managed-by=Helm --overwrite
  done
  ```

## SQL Server builds

`version` is the major version (`2019`, `2022`, `2025`). The exact build comes from `cumulativeUpdate` and `baseOs`:

```yaml
spec:                          # instanceTemplate on a SqlAvailabilityGroup
  version: "2022"
  cumulativeUpdate: CU25       # → mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04
```

Without `cumulativeUpdate`, a server runs the operator's default CU for its version. An operator upgrade can bring a newer default, which is applied only after the annotation `mssql-operator.evariops.com/approve-engine-upgrade` is set on the resource with that CU as its value, for example `CU10` (`operator.engineUpgradePolicy: Approval`, the default). The `EngineUpgradePending` condition names the CU to approve. Pinning `cumulativeUpdate` avoids this.

The webhook refuses builds that Microsoft did not publish for the requested OS base. It also refuses SQL Server 2025 builds older than CU7, which do not start when the node pins the container's CPUs ([Failure modes](../failure-modes.md#sql-server-2025-does-not-start-on-a-pinned-cpuset)); an existing resource that already runs such a build can still be edited, with a warning. Tags newer than the operator's snapshot of published tags are accepted with a warning. Downgrades are refused, because SQL Server cannot open databases upgraded by a newer build; the annotation `mssql-operator.evariops.com/allow-version-downgrade: "true"` overrides this when a restore plan exists. Floating tags such as `2022-latest` are refused.

With a private mirror, set `image` together with `imageTag` or a `@sha256:` digest. The operator then cannot read the build from the tag, and the downgrade and published-build checks no longer apply.

On an AG, a build change is applied as a [rolling update](availability-groups.md#rolling-updates). During the rollout, the automatic failover never promotes a replica that runs an older build, and the automatic failover with data loss is disabled. `status.productVersion` (instance) and `status.primaryProductVersion` (AG) report the running build. With full-text indexes on SQL Server 2017 or 2019, check `upgrade_option` before upgrading ([Failure modes](../failure-modes.md#slow-restarts-with-full-text-indexes)).

## Automation modes and approvals

Every disruptive or destructive action goes through the [actuation gateway](../architecture/safety.md#actuation-gateway). Two settings control what it lets through: the automation mode of each resource, and approvals for destructive actions.

### Automation mode

```yaml
spec:
  automation:
    mode: Apply        # Observe | Plan | Apply (default)
```

In `Apply` mode, the operator acts. In `Plan` and `Observe` modes, it records what it would do and runs no disruptive or destructive action. `Observe` is intended for a new resource or for a server being taken over, to review the operator's decisions before letting it act. Requests made by annotation in `Plan` or `Observe` mode, such as `failover-to`, stay in place and run when the mode returns to `Apply`.

An operator-wide ceiling caps the mode of every resource. It is set with the Helm value `operator.automation.maxMode`: with `Plan`, every resource runs in `Plan` at most, whatever its own mode.

### Destructive actions

Disruptive actions interrupt service without losing data: a lossless failover, the re-seed of a secondary, fencing a pod, disabling a login. They run in `Apply` mode.

Destructive actions lose something that exists nowhere else: a failover with data loss, the rejoin of a divergent replica, removing a database from an AG, dropping a database, a login, a database user, a procedure or a job. In `Apply` mode, a destructive action runs without approval only in these cases:

- it cleans up a resource that is being deleted;
- the spec requests it, for example a database removed from a `SqlUser`, or `allowAutomaticDataLossFailover`;
- a dedicated annotation requests it: `force-failover-to` with `allow-data-loss`, or `approve-data-loss-resume`;
- its type is listed in `spec.automation.preAuthorized`, within a budget of one action per resource per hour and three per hour for the whole operator (Helm value `operator.automation.maxDestructivePerHour`).

In any other case the action waits, and the event `ActionRequiresApproval` gives its id. The approval annotation names that id:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql \
  mssql-operator.evariops.com/approve='RemoveDatabaseFromAg:Orders:3:1a2b3c4d'
```

The approval is consumed once the action has run. A type can be pre-authorized instead:

```yaml
spec:
  automation:
    preAuthorized: [DropProcedure]
```

The destructive types are `ForceFailoverDataLoss`, `ResumeDivergentReplica`, `RemoveDatabaseFromAg`, `DropLogin`, `DropDatabaseUser`, `DropProcedure`, `DeleteJob` and `DropDatabase`. Regenerating the service master key and generating a machine key over existing data are never automatic.

### Plans

```console
$ kubectl get operatorplans -n sql
NAME                       RESOURCE                      ACTION     RISK         DECISION   EXECUTED   ID
orders.failover.241a8913   SqlAvailabilityGroup/orders   Failover   Disruptive   Withheld   false      Failover:orders-1:15:241a8913
```

`operatorplans` lists the latest decisions for each resource: executed, withheld, or waiting for approval. The list is kept in the leader's memory, not in etcd, and is emptied by a restart. Each decision is also published as an event: `ActionPlanned`, `ActionRequiresApproval` or `ActionDenied`.

## Safety levels

The safety level of an AG selects which parts of the failover machinery are active. The content of each level is described in [Safety mechanisms](../architecture/safety.md#safety-levels).

| Level | Adds |
|---|---|
| 0 | the original failover logic, with its fixes |
| 1 | the control record, the operator epoch, and the epoch checks and locks on the SQL Server side |
| 2 (experimental) | the quorum watchdog, elections by quorum, listener routing by pod name |
| 3 (experimental) | event-driven supervision |

An AG runs at the level given by its annotation `mssql-operator.evariops.com/safety-level` (0 when absent), capped by the Helm value `operator.safetyLevelMax` (1 by default). The level above the effective one runs in shadow mode: its decisions are computed and logged, not applied.

The recommended setting is `safety-level: "1"` on every AG with the ceiling at 1. The ceiling should not be raised to 2 in production, because the timing assumption of level 2 does not hold yet ([Safety mechanisms](../architecture/safety.md#level-2-protocol-experimental)).

## Circuit breakers

`operator.targetCircuitBreakers` (`Shadow` by default) limits the cost of an unreachable server. In `Enforce` mode, a connection to a server that the last health read could not reach fails immediately instead of waiting for its timeouts and retries, so one dead server does not slow down the reconciliation of the others. `Shadow` logs what it would have refused.

## Monitoring

### NeedsAttention

Every resource has a `NeedsAttention` condition. It is `True` only when a human decision is required, and its reason and message describe the decision. It is exported as the gauge `mssqloperator_needs_attention{kind,namespace,name}`. The following command lists the resources that need attention:

```bash
kubectl get sqlavailabilitygroup,sagdb,sqldb,sqlinstance -A \
  -o custom-columns='KIND:.kind,NS:.metadata.namespace,NAME:.metadata.name,ATTENTION:.status.conditions[?(@.type=="NeedsAttention")].message'
```

[Failure modes](../failure-modes.md#triage) maps each reason to the corresponding failure and the action to take.

### Alerts

`prometheusRule.enabled=true` installs reference alerting rules for prometheus-operator.

| Alert | Severity | Meaning |
|---|---|---|
| `MsSqlOperatorNeedsAttention` | critical | a resource needs a decision; the only alert that should page |
| `MsSqlOperatorAgNotHealthChecked` | critical | an AG has not been checked for 2 minutes, so a primary failure would go undetected |
| `MsSqlOperatorNoLeader` | critical | no operator replica holds the leadership, nothing is reconciled |
| `MsSqlOperatorWatchdogSilent` | critical | a quorum watchdog job stopped running (safety level 2) |
| `MsSqlOperatorReplicaResolving` | warning | a replica stays `RESOLVING` while another one is primary |
| `MsSqlOperatorActionAwaitingApproval` | warning | a destructive action is waiting for approval |
| `MsSqlOperatorWorkQueueStuck`, `MsSqlOperatorOperationStuck` | warning | the work queue is stuck, or a failover or rolling update has lasted more than 30 minutes |
| `MsSqlOperatorWritesDropped`, `MsSqlOperatorWatchSilent`, `MsSqlOperatorWatchLosses`, `MsSqlOperatorAgHeartbeatFailing` | warning | the operator itself is degraded |

### Events, view API and metrics

Events are published on the resources and can be read with `kubectl describe` or `kubectl get events -n sql --field-selector involvedObject.name=orders`. The main ones cover failovers (`FailoverCompleted`, `ReplicaFenced`, `ResurrectedPrimaryDemoted`), re-seeds (`ReplicaReseeded`), log backups (`LogBackedUp`), durability (`RequiredSyncLowered`, `DurabilityFloorHoldingWrites`), gateway decisions (`ActionPlanned`, `ActionRequiresApproval`) and node maintenance (`MaintenanceSignal`, `EvictionDelayed`, `MaintenanceFailover`, `MaintenanceDelayExpired`; see [Node maintenance](node-maintenance.md#troubleshooting)). Events are deduplicated by reason: a new event replaces the previous one with the same reason.

Live data that is not stored in the status (replica state, backups recorded in msdb, plans) is served by the [view API](../reference/operator.md#view-api): `kubectl get agreplicastates`, `logchains`, `operatorplans`, and so on.

The operator pods serve Prometheus metrics on port 8080 (`/metrics`), listed in the [reference](../reference/operator.md#metrics). A Grafana dashboard is provided in [`grafana-dashboard.json`](../observability/grafana-dashboard.json).

### Traces, logs and probes

Each operation (failover, fencing, re-seed, TDE rotation, deployment) produces one trace with its steps as spans, exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set on the operator. Logs are written one line per entry (`logFormat: simple`) or as JSON (`json`), with the resource kind, name and reconcile id on each line. `/healthz` and `/readyz` are served on port 8080.

## Production hardening

The rationale for these settings is in [Security model](../architecture/security.md).

### Tenancy

Resources only target servers in their own namespace, so anyone who can create a `SqlUser` or a `SqlConfiguration` in a namespace controls the SQL Servers of that namespace. Run one instance or AG per team, in the team's namespace. Bind the `view-reader` ClusterRole with namespaced RoleBindings, so that each team reads only its own namespace.

### Admission webhook

Keep `webhook.enabled: true` and `failurePolicy: Fail`, so that every resource is validated before it is stored. The most sensitive rules are checked again at apply time, but the webhook is what rejects an invalid manifest at `kubectl apply`. With GitOps tools that render the chart with `helm template` (Argo CD), use cert-manager for the webhook certificate (`webhook.certManager.enabled`).

### TLS

Clients get the certificate configured on the server:

```yaml
spec:                        # SqlInstance; under instanceTemplate on a SqlAvailabilityGroup
  tls:
    secretName: sql-tls      # kubernetes.io/tls Secret, cert-manager works
    forceEncryption: true
```

The SANs must cover `<instance>.<namespace>.svc.cluster.local` for an instance, and `*.<ag>-headless.<namespace>.svc.cluster.local` plus the listener Services for an AG. SQL Server reads the certificate at startup, so a renewed certificate is used after the next restart.

By default, the operator's connection to SQL Server is encrypted but the server certificate is not verified (`sqlConnection.trustServerCertificate: true`). Once the servers have certificates, enable verification:

```yaml
sqlConnection:
  encrypt: Strict                # SQL Server 2022+; Mandatory also validates on 2019
  trustServerCertificate: false
  caSecretName: sql-ca           # Secret in the operator namespace, CA in ca.crt
```

### Network policies

The operator's NetworkPolicy is enabled by default. `metricsFrom` restricts who can scrape metrics, and `webhookFrom` or `webhookFromApiServerOnly` who can call the webhook. Metrics are served over plain HTTP and must not be exposed outside the cluster.

For the SQL Server pods, `spec.networkPolicy` creates a CiliumNetworkPolicy. Port 1433 is then reachable only from the same namespace, from `allowedNamespaces` and from the operator, and egress is limited to DNS, the other replicas and the destinations listed in `networkPolicy.egress` (SMTP relay, backup storage, linked servers).

### Operator privileges

The operator reads Secrets in every namespace and connects to SQL Server as `sa`, so access to its namespace and ServiceAccount should be restricted accordingly. Keep the breakout controller in its own Deployment (`breakout.dedicated: true`). `breakout.fencingDriver: none` makes it report without acting, and `breakout.enabled: false` removes it when no AG uses `ReadWriteOnce` volumes. Give the operator a `priorityClassName`, since it serves cluster-wide APIs.

### Secrets at rest

Enable etcd encryption at rest, and copy the key material outside the cluster ([Backups and encryption](protecting-data.md#copies-outside-the-cluster)).

### Image verification

Only release images are signed: the tags `X.Y.Z`, `X.Y`, `X` and `latest`, built by the release workflow from the git tag `vX.Y.Z`. They are signed with cosign (keyless, GitHub OIDC) and carry an SBOM and a build provenance attestation. The images built from `main` and from pull requests (tags `main`, `sha-*`, `pr-*`) are development builds and are not signed, so an admission policy pinned on the release identity refuses them.

Verify an image against the exact identity of the release workflow, for the version you install:

```bash
cosign verify ghcr.io/evariops/mssql-operator@<digest> \
  --certificate-identity https://github.com/Evariops/mssql-operator/.github/workflows/release.yml@refs/tags/v<version> \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
gh attestation verify oci://ghcr.io/evariops/mssql-operator@<digest> --repo Evariops/mssql-operator
```

The chart is signed the same way:

```bash
cosign verify ghcr.io/evariops/charts/mssql-operator@<digest> \
  --certificate-identity https://github.com/Evariops/mssql-operator/.github/workflows/release.yml@refs/tags/v<version> \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```
