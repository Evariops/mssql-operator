# Upgrading

## ⚠️ Upgrading to the reliability release

**This upgrade restarts every SQL Server pod.** Read this section before rolling it out.

### Why every pod restarts

The remediation changes the pod template of both StatefulSets — a `preStop` hook (CHECKPOINT before
shutdown), a readiness probe that waits for crash recovery to finish, a lower `max server memory` and
`fsGroupChangePolicy: OnRootMismatch`. A changed pod template is, by definition, a rollout:

- **SqlInstance**: the StatefulSet is `RollingUpdate` by default, so the pod restarts as soon as the
  operator reconciles it. Set `spec.updateStrategy.type: OnDelete` **before** upgrading if you want to
  choose the moment (that field is now wired — it used to be ignored).
- **SqlAvailabilityGroup**: the AG-aware rolling update takes over — one replica at a time, gated on
  AG health, with a planned failover before the primary is updated (`failoverBeforePrimaryUpdate`
  defaults to `true`). Expect **one failover per AG**.

### Do this first

1. **Stagger the rollout.** Upgrade the operator in one namespace/cell at a time; the operator
   reconciles every CR it watches, so a fleet-wide install restarts the fleet.
2. **Pause what must not move.** Annotate the critical resources before the upgrade and remove the
   annotation when you are ready for them to roll:
   ```bash
   kubectl annotate sqlavailabilitygroup my-ag mssql-operator.evariops.com/reconcile-paused=true
   ```
   A paused resource now says so in its status (`ReconcilePaused` condition, `Ready=False`) and the
   breakout controller leaves it alone too.
3. **Check `upgrade_option` on full-text instances**. A rolling
   restart of an instance whose full-text is set to `Rebuild` repopulates every catalogue.

### Behaviour changes to know about

| Change | Impact |
|---|---|
| Failover demotes the old primary (`SET (ROLE = SECONDARY)`) and requires **proven** fencing when it cannot | A failover whose old primary can be neither demoted nor fenced is now **refused** (event `FailoverAborted`) instead of creating two primaries. Fencing needs a CSI driver with `attachRequired: true`; with NFS/local-path, an unreachable primary must be fenced externally. |
| Key-material Secrets lose their `ownerReference` | `<name>-machine-key`, `<name>-smk-backup`, `<name>-smk-backup-password` and `<name>-sa-applied` now **survive** the deletion of their CR (deleting them destroyed the ability to decrypt the surviving PVCs). Delete them by hand when you decommission an instance for good. |
| `spec.replicas.count` immutability is now enforced by a CEL rule on the CRD | An existing manifest that drifted from the provisioned count will be rejected at apply time. |
| A `SqlUser` targeting `sa` is rejected | It used to rewrite the sa password and lock the operator out. |
| A `SqlAgentJob` whose owner is `sa` (or unset) requires `job.allowDangerous: true` | Its steps run as sysadmin and can bypass the sp_configure allowlist. |
| `targetMatchLabels: {}` on a `SqlConfiguration` is rejected | An empty selector matched **every** instance in the namespace. |
| The operator chart defaults to `replicaCount: 2` | With `viewApi` enabled, a single replica is a cluster-wide SPOF (its APIService discovery blocks namespace deletion cluster-wide). |
| The webhook certificate is regenerated when it is within 30 days of expiry | The previous chart reused it forever; it expired one year after install and blocked all CRD writes. |

## CRDs are now managed by Helm (templates/ + `helm.sh/resource-policy: keep`)

Since the M7 change, the CRDs live in `templates/crds/` instead of the special `crds/`
directory. Consequences:

- **Upgrades update the CRD schemas.** Previously Helm only applied `crds/` on first
  install: every schema enrichment shipped after that silently never reached existing
  clusters (the API server pruned unknown fields).
- **Uninstall keeps the CRDs** (`helm.sh/resource-policy: keep`), so existing CRs and the
  data they describe survive a chart removal.

### Adopting CRDs installed by an older chart version

Helm refuses to update resources it does not own. If your cluster has CRDs installed by a
pre-M7 chart (or by `kubectl apply`), label them for adoption **once** before upgrading
(replace release name/namespace if yours differ):

```bash
for crd in $(kubectl get crd -o name | grep 'mssql-operator.evariops.com'); do
  kubectl annotate "$crd" meta.helm.sh/release-name=mssql-operator --overwrite
  kubectl annotate "$crd" meta.helm.sh/release-namespace=mssql-operator-system --overwrite
  kubectl label "$crd" app.kubernetes.io/managed-by=Helm --overwrite
done
```

## API group rename (v1alpha1 breaking change)

The API group moved from `mssql.operator` to `mssql-operator.evariops.com`
(`apiVersion: mssql-operator.evariops.com/v1alpha1`). The old group is gone — there is no
conversion. Pre-rename installations must:

1. Export existing CRs, rewrite `apiVersion`, delete the old CRs (mind finalizers).
2. Delete the old CRDs (`kubectl delete crd ...mssql.operator`).
3. Install the new chart version and re-apply the rewritten CRs.

## `target.namespaces` removed

See [tenancy.md](tenancy.md) — cross-namespace targeting was removed; CRs carrying the
field will have it pruned and resolve in their own namespace.
