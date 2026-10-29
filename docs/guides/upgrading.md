# Upgrading

## Upgrade the operator

```bash
helm upgrade mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> --namespace mssql-operator-system
```

- The CRDs are chart templates: `helm upgrade` updates them. `helm uninstall` keeps them (and your
  resources).
- Read the release notes first: an operator release can change the pod template of the SQL Server
  StatefulSets, and every changed template restarts SQL Server. AGs roll one replica at a time with a
  planned failover; across the cluster, `operator.maxConcurrentRestarts` (1) restarts happen at once.
- Upgrade one namespace or environment at a time if you can.

To hold a resource during an upgrade, pause it, and remove the annotation when it may roll:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/reconcile-paused=true
kubectl annotate sqlavailabilitygroup orders -n sql mssql-operator.evariops.com/reconcile-paused-
```

A paused resource reports `ReconcilePaused` and is left alone, breakout included. A `SqlInstance` can
also use `updateStrategy.type: OnDelete` to restart only when you delete its pod.

### Upgrading to the reliability release (October 2026)

- The StatefulSets of the AGs are recreated around their pods (`podManagementPolicy: Parallel`): pods
  and volumes are kept. Then every SQL Server pod restarts once (new in-memory key volume, DNS
  settings): one rolling update, so one planned failover, per AG.
- A `SqlControlRecord` appears for each AG and instance.
- Existing AGs keep the `Legacy` durability. To move one to `Quorum`, see
  [Availability groups](availability-groups.md#choose-the-durability).
- `SqlAvailabilityGroupDatabase` without `logBackup.mode` reports `LogBackupModeDeprecated`: set
  `Local` or `Disabled`.
- Safety level 0 remains the default. Annotate AGs with `mssql-operator.evariops.com/safety-level: "1"`
  once you have watched level 1 run in shadow (see [Automation](automation.md#safety-levels)).

## SQL Server builds

`version` is the major version (`2019`, `2022`, `2025`); the build comes from `cumulativeUpdate` and
`baseOs`:

```yaml
spec:                          # instanceTemplate on a SqlAvailabilityGroup
  version: "2022"
  cumulativeUpdate: CU25       # → mcr.microsoft.com/mssql/server:2022-CU25-ubuntu-22.04
```

- Without `cumulativeUpdate`, the server runs the operator's default CU for that version. Upgrading
  the operator can bring a newer default: it waits for
  `mssql-operator.evariops.com/approve-engine-upgrade: "true"` on the resource
  (`operator.engineUpgradePolicy: Approval`, the default). Pin `cumulativeUpdate` to decide yourself.
- The operator knows which tags Microsoft actually published, and refuses a build that does not exist.
- **Builds only move forward.** SQL Server cannot open databases upgraded by a newer build, so a
  downgrade is refused. If you have a restore plan, annotate
  `mssql-operator.evariops.com/allow-version-downgrade: "true"`.
- Floating tags (`2022-latest`) are refused: the engine would change under the databases.
- A private mirror: set `image` (and `imageTag`, or a `@sha256:` digest). The operator then cannot read
  the build, and the two rules above no longer apply to it.

Watch the upgrade land: `status.productVersion` (instance) or `status.primaryProductVersion` (AG) is
the build actually running.

Full-text indexes: check the `upgrade_option` before a CU upgrade, see the
[full-text runbook](../runbooks/fulltext.md).

## Breaking changes of earlier versions

- **API group**: `mssql.operator` became `mssql-operator.evariops.com` (no conversion). Export your
  resources, rewrite `apiVersion`, delete the old resources and CRDs, install, re-apply.
- **`target.namespaces` removed**: resources only target their own namespace.
- **`databaseFilter.pattern` is anchored** (`^…$`): a pattern relying on substring matching now
  matches fewer databases; prefix it with `.*` if needed.
- **CRDs installed by an old chart or `kubectl apply`**: label them for Helm once before upgrading:

  ```bash
  for crd in $(kubectl get crd -o name | grep 'mssql-operator.evariops.com'); do
    kubectl annotate "$crd" meta.helm.sh/release-name=mssql-operator --overwrite
    kubectl annotate "$crd" meta.helm.sh/release-namespace=mssql-operator-system --overwrite
    kubectl label "$crd" app.kubernetes.io/managed-by=Helm --overwrite
  done
  ```
