# Upgrading

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
