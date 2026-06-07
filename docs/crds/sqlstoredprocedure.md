# `SqlStoredProcedure`

> Declaratively deploys T-SQL stored procedures to one or more SQL Server databases from inline definitions or a GitHub repository.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlStoredProcedure` · scope: Namespaced · plural: `sqlstoredprocedures`

## Minimal example

```yaml
apiVersion: mssql.operator/v1alpha1
kind: SqlStoredProcedure
metadata:
  name: my-procs
  namespace: mssql-demo
spec:
  target:
    instances:
      - my-sql-instance
  databases: [MyAppDB]
  inline:
    - name: usp_Ping
      definition: |
        CREATE OR ALTER PROCEDURE [dbo].[usp_Ping]
        AS
        BEGIN
            SET NOCOUNT ON;
            SELECT 1 AS Ok;
        END
```

## Overview

A `SqlStoredProcedure` resource resolves target SQL instances or availability groups, selects the databases to operate on, and deploys stored procedures into each. Provide procedure definitions inline in the spec, or fetch them from files in a GitHub repository at a given tag or commit. When a GitHub source uses `sync.followLatestRelease`, the operator tracks the repository's latest matching release and re-deploys when it changes. Deployments are idempotent. The operator hashes each procedure and re-applies it only when its definition changes.

## Spec

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `target` | object (`TargetSelector`) | yes | `{}` | Selects the SQL instances or availability groups to deploy to. See [target](#target). |
| `databases` | string[] | no |  | Explicit list of database names to deploy into, combined with `databaseFilter`. If both are omitted, the operator's database filter decides which discovered databases are eligible. |
| `databaseFilter` | object | no |  | Filters discovered databases. See [databaseFilter](#databasefilter). |
| `inline` | object[] | conditional |  | Inline procedure definitions. At least one of `inline` or `github` must be set. See [inline[]](#inline). |
| `github` | object | conditional |  | GitHub source for procedure files. At least one of `inline` or `github` must be set. See [github](#github). |
| `deploymentOptions` | object | no | see defaults | Controls how procedures are applied. See [deploymentOptions](#deploymentoptions). |
| `sync` | object | no |  | Periodic and release-following sync settings for GitHub sources. See [sync](#sync). |

> `inline` and `github` are **not** mutually exclusive. If both are present, procedures from both sources are gathered and deployed together.

### `target`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `target.instances` | string[] | no |  | Names of `SqlInstance` resources to target. |
| `target.availabilityGroups` | string[] | no |  | Names of `SqlAvailabilityGroup` resources to target (procedures are deployed on the primary replica only). |
| `target.matchLabels` | map[string]string | no |  | Label selector matching `SqlInstance` or `SqlAvailabilityGroup` resources. |
| `target.namespaces` | string[] | no |  | Namespaces to search. Cannot be used alone, so combine it with a more specific selector. |

### `databaseFilter`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `databaseFilter.pattern` | string | no |  | Pattern used to include databases by name. |
| `databaseFilter.exclude` | string[] | no |  | Database names to exclude. |
| `databaseFilter.matchLabels` | map[string]string | no |  | Label selector for databases. |

### `inline[]`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `inline[].name` | string | yes | `""` | Logical name of the procedure (used for ordering and status). |
| `inline[].schema` | string | no | `dbo` | Schema the procedure belongs to. |
| `inline[].definition` | string | yes | `""` | Full T-SQL definition (e.g. a `CREATE OR ALTER PROCEDURE` statement). |

### `github`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `github.repository` | string | yes | `""` | Repository in `owner/repo` form. |
| `github.authSecretRef` | object | no |  | Reference to a Secret holding the GitHub token. See [github.authSecretRef](#githubauthsecretref). |
| `github.ref` | object | no |  | Git ref to fetch from. See [github.ref](#githubref). When omitted or unresolved, the default branch (`HEAD`) is used. |
| `github.paths` | object[] | no |  | List of repository paths to fetch procedures from, each with an optional schema override. See [github.paths[]](#githubpaths). |
| `github.include` | string[] | no |  | Glob patterns of files to include. |
| `github.exclude` | string[] | no |  | Glob patterns of files to exclude. |

#### `github.ref`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `github.ref.tag` | string | no |  | Git tag to fetch. When `sync.followLatestRelease` is enabled, this is overwritten with the resolved latest release tag. |
| `github.ref.sha` | string | no |  | Commit SHA to fetch. |

> The ref model supports only `tag` and `sha`. There is no `branch` or `commit` field.

#### `github.paths[]`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `github.paths[].path` | string | yes | `""` | Repository path (file or directory) to read `.sql` files from. |
| `github.paths[].schema` | string | no | `dbo` | Schema applied to procedures from this path when not derivable from the SQL. |

#### `github.authSecretRef`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `github.authSecretRef.name` | string | yes | `""` | Name of the Secret in the resource's namespace. |
| `github.authSecretRef.key` | string | yes | `""` | Key within the Secret holding the token. |

### `deploymentOptions`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `deploymentOptions.strategy` | string (enum) | no | `CreateOrAlter` | Apply strategy. Allowed: `CreateOrAlter`, `DropAndCreate`. With `DropAndCreate` the existing procedure is dropped (`DROP PROCEDURE IF EXISTS`) before deploying. Only consulted in the non-transactional path. |
| `deploymentOptions.transactional` | bool | no | `true` | When `true`, changed procedures for a database are validated then applied together inside a single transaction (rolled back on error). |
| `deploymentOptions.executionOrder` | string (enum) | no | `Alphabetical` | Order procedures are processed in. Allowed: `Alphabetical` (sort by name) or any other value (spec/source order). |
| `deploymentOptions.validateSyntax` | bool | no | `true` | When `true`, each changed procedure is parsed (`SET PARSEONLY ON`) before deploying. A parse failure aborts that database's deployment. |
| `deploymentOptions.dryRun` | bool | no | `false` | When `true`, the deployment is planned and status is updated but no SQL is executed against the database. |

### `sync`

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `sync.intervalMinutes` | int32 | no | `30` | Resync interval (minutes). |
| `sync.followLatestRelease` | bool | no | `false` | When `true` (GitHub sources only), the operator resolves the latest matching GitHub release and overrides `github.ref.tag` with it before fetching. |
| `sync.releaseFilter` | string | no |  | Filter applied when selecting the latest release (e.g. `v*`). |

## Status

| Field | Type | Description |
|---|---|---|
| `phase` | string | Lifecycle phase. Observed values: `Pending` (initial), `Deployed` (all targets succeeded), `PartiallyDeployed` (one or more targets errored). |
| `observedGeneration` | int64 | `metadata.generation` last reconciled. |
| `source` | string | Origin of the deployed procedures: `Inline` or `GitHub`. |
| `sourceRef` | string | For GitHub sources, the resolved `repository@<tag|sha|HEAD>`. Null for inline-only. |
| `targets` | int32 | Number of targets (instances/AGs) matched by the selector. |
| `failedTargets` | int32 | Targets that could not be reconciled this pass. |
| `desiredProcedures` | int32 | Procedures defined in the spec (inline + fetched). |
| `deployedProcedures` | int32 | Procedure deployments actually applied across all targets and databases. |
| `lastDeployedRevision` | string | The source revision (GitHub tag/sha or inline) last deployed. |
| `conditions` | object[] | Standard conditions. The operator maintains a `Ready` condition (`type`, `status`, `reason`, `message`, `lastTransitionTime`). Reasons include `NoMatch`, `AllProceduresDeployed`, `PartialError`. |

This is a slim, etcd-light status: a convergence signal plus O(1) roll-up scalars. The former per-target `targetStatuses[]` array — and its per-database / per-procedure `deployedProcedures[]` trees (name, schema, deployed, hash, lastDeployed) — has been removed.

`kubectl get sqlstoredprocedure` shows: `PHASE` (`.status.phase`), `DEPLOYED` (`.status.deployedProcedures`), `DESIRED` (`.status.desiredProcedures`), `TARGETS` (`.status.targets`), and `AGE`.

**Live detail.** Per-target, per-database, and per-procedure deployment detail (including the definition hash used for change detection) is no longer stored on the resource. Instead:

- It is served live via the `view.mssql.operator` aggregated read API — `kubectl get storedproceduredeployments` (coming with the aggregated read API).
- Each procedure deployment and each target failure is emitted as a Kubernetes Event (`kubectl describe sqlstoredprocedure <name>`).
- Counts over time are exposed as Prometheus metrics (`mssqloperator_storedproc_*`, `mssqloperator_targets_*`).

## More examples

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: github-token
  namespace: mssql-demo
type: Opaque
stringData:
  token: "ghp_your_token_here"
---
# GitHub source following the latest release
apiVersion: mssql.operator/v1alpha1
kind: SqlStoredProcedure
metadata:
  name: github-procs
  namespace: mssql-demo
spec:
  target:
    instances:
      - my-sql-instance
  databases: [MyAppDB]
  github:
    repository: my-org/sql-procedures
    paths:
      - path: procedures/
        schema: dbo
    include:
      - "*.sql"
    ref:
      tag: v1.2.0
    authSecretRef:
      name: github-token
      key: token
  sync:
    followLatestRelease: true
    releaseFilter: "v*"
    intervalMinutes: 15
  deploymentOptions:
    strategy: CreateOrAlter
    validateSyntax: true
    transactional: true
    executionOrder: Alphabetical
```

## Validation & behaviour

**Webhook validation** (`SqlStoredProcedureValidator`, returns HTTP 422 on failure):

- `target` must specify at least one selector (`namespaces`, `matchLabels`, `instances`, or `availabilityGroups`).
- `target` with **only** `namespaces` is rejected as too broad. Add `matchLabels`, `instances`, or `availabilityGroups`.
- At least one source is required: at least one of `inline` or `github` must be set.

**Reconcile behaviour** (`SqlStoredProcedureController`):

- If no targets match the selector, the resource is marked `Ready=False` / `NoMatch` and reconciliation ends successfully.
- Procedures are gathered from both sources when present. For inline, the `name` and `schema` come from the spec. For GitHub, the procedure name and schema are parsed from the SQL (`CREATE [OR ALTER] PROCEDURE [schema].[name]`). If not parseable, the file name is used and schema defaults to `dbo`.
- For `SqlAvailabilityGroup` targets, the operator locates the primary replica and deploys there only. For `SqlInstance` targets it connects directly to the instance.
- Eligible databases are computed from `databases` plus `databaseFilter` against the databases discovered on each target.
- **Change detection:** each procedure definition is normalized and SHA-256 hashed. A procedure is only (re)deployed when its hash differs from the definition currently deployed in the database, making reconciliation idempotent. (With the slim status the per-procedure hash is no longer persisted on the resource; it is exposed live via the `view.mssql.operator` API — `kubectl get storedproceduredeployments`.)
- **Execution order:** `Alphabetical` sorts changed procedures by name. Any other value preserves source order.
- **Transactional apply** (default): changed procedures for a database are collected, optionally syntax-validated, then executed together in one transaction that rolls back on any error.
- **Non-transactional apply:** procedures are deployed one by one. With `strategy: DropAndCreate` the existing procedure is dropped first, then deployed.
- **Syntax validation:** when `validateSyntax` is `true`, each changed procedure is checked with `SET PARSEONLY ON`. A failure aborts the current database's deployment.
- **Dry run:** when `dryRun` is `true`, status is computed but no SQL is executed.
- **Release-following sync:** when `github` is set and `sync.followLatestRelease` is `true`, the latest release tag (filtered by `sync.releaseFilter`) is resolved and overrides `github.ref.tag` before files are fetched.
- On completion the phase is `Deployed` (no errors) or `PartiallyDeployed` (any target errored), `source`/`sourceRef` are recorded, the `Ready` condition is set, and a `ProceduresDeployed` event is published.

## Related

- [`SqlInstance`](./sqlinstance.md)
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md)
