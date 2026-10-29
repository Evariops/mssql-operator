# SqlStoredProcedure

Stored procedures deployed into the databases of the instances and availability groups it targets,
from inline T-SQL or from `.sql` files in a GitHub repository (optionally following its releases).

Guide: [Configuration, jobs and procedures](../guides/configuration.md).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlStoredProcedure
metadata:
  name: orders-procs
  namespace: sql
spec:
  target:
    availabilityGroups: [orders]       # deployed on the primary; the AG replicates it
  databases: [Orders]
  inline:
    - name: usp_Ping
      definition: |
        CREATE OR ALTER PROCEDURE dbo.usp_Ping AS SELECT 1 AS ok;
```

From GitHub instead:

```yaml
  github:
    repository: my-org/orders-db
    authSecretRef: { name: github-token, key: token }   # private repositories
    paths: [{ path: procedures/ }]
  sync:
    followLatestRelease: true
    releaseFilter: "v*"
```

## Main fields

| Field | Default | What it does |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, same namespace. |
| `databases` / `databaseFilter` | | Databases to deploy into (names, or a `pattern` with `exclude`). |
| `inline[]` | | `name`, `schema` (`dbo`), `definition`. |
| `github` | | `repository` (`owner/repo`), `ref.tag` or `ref.sha` (default branch otherwise), `paths[]`, `include` / `exclude` globs, `authSecretRef`. |
| `sync.intervalMinutes` | `30` | How often GitHub sources are checked. |
| `sync.followLatestRelease` / `releaseFilter` | `false` | Deploy the latest release matching the filter. |
| `deploymentOptions.transactional` | `true` | Changed procedures of a database are applied in one transaction. |
| `deploymentOptions.dryRun` | `false` | Plan only, run nothing. |
| `deploymentOptions.deployAs` | `LeastPrivilege` | Deploy through an operator-managed login that is only `db_ddladmin` on the targeted databases. `Sa` requires the target to carry `mssql-operator.evariops.com/allow-sa-deployments: "true"`. |

Only procedures whose definition changed are redeployed (compared by hash). The GitHub source needs
egress to `api.github.com` (allowed by the operator's network policy by default).

## Status

```console
$ kubectl get sqlstoredprocedure -n sql
NAME           PHASE      DEPLOYED   DESIRED   TARGETS   AGE
orders-procs   Deployed   1          1         1         2d
```

Per-database detail, live: `kubectl get storedproceduredeployments -n sql`.

Deleting the resource drops the procedures it deployed.
