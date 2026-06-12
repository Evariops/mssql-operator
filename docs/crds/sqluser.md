# `SqlUser`

> Declarative SQL Server login, database users and role/permission membership provisioned across one or more targeted instances.

`apiVersion: mssql.operator/v1alpha1` · `kind: SqlUser` · scope: Namespaced · plural: `sqlusers`

## Minimal example

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: app-user-password
  namespace: mssql-demo
type: Opaque
stringData:
  password: "AppUs3rP@ss!"
---
apiVersion: mssql.operator/v1alpha1
kind: SqlUser
metadata:
  name: app-user
  namespace: mssql-demo
spec:
  target:
    instances:
      - my-sql-instance
  login:
    name: app_login
    passwordSecretRef:
      name: app-user-password
      key: password
  databasePermissions:
    - databases: [MyAppDB]
      roles: [db_datareader]
```

## Overview

A `SqlUser` manages a SQL Server **login** plus the **database users** that map to it and their **role membership** and **permissions** on every instance selected by its target. On each reconcile the operator resolves the target selector to concrete instances (and Availability Group replicas), creates the login if it is missing (or re-syncs its password from the referenced Secret), and grants the requested server roles and server permissions. On the primary it then creates a database user per matched database and adds the requested database roles and schema permissions.

It also supports time-bounded access via an `expiration` block (disable or delete the login at a deadline, optionally terminating active sessions), with automatic reactivation if the deadline is pushed into the future. Per-target `overrides` let you vary database and server permissions for specific instances or Availability Groups. On deletion a finalizer drops the database users and login from every resolved target.

## Spec

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `target` | object | Yes | `{}` | Selector for the instances/AGs to provision on. See [target](#target). Must specify at least one of `matchLabels`, `instances`, or `availabilityGroups`. Resolution is confined to the CR's namespace. |
| `login` | object | Yes |   | The SQL Server login to manage. See [login](#login). |
| `expiration` | object | No |   | Time-bound the access. See [expiration](#expiration). |
| `databasePermissions` | object[] | No |   | Database-user mappings plus role and schema grants. See [databasePermissions](#databasepermissions). |
| `serverRoles` | string[] | No |   | Server-level roles to add the login to (e.g. `sysadmin`, `dbcreator`). Applied on every connection and replica. |
| `serverPermissions` | string[] | No |   | Server-level permissions to `GRANT` to the login (e.g. `VIEW SERVER STATE`). Validated against an allow-list before execution. |
| `overrides` | object[] | No |   | Per-target overrides of database and server permissions. See [overrides](#overrides). |

### target

`spec.target` (`TargetSelector`). At least one selector is required. Targets are always resolved in the CR's own namespace (cross-namespace targeting is not supported — see docs/tenancy.md).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `matchLabels` | map[string]string | No |   | Label selector matching `SqlInstance` or `SqlAvailabilityGroup` resources. |
| `instances` | string[] | No |   | Explicit `SqlInstance` names. |
| `availabilityGroups` | string[] | No |   | Explicit `SqlAvailabilityGroup` names. |

### login

`spec.login` (`LoginSpec`).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | string | Yes | `""` | Login name. Also used verbatim as the database user name when database users are created. |
| `type` | string | No | `SqlLogin` | Login type. (Currently only SQL-authenticated logins are created with a password.) |
| `passwordSecretRef` | object | No |   | Reference to the Secret holding the login password. See [secret references](#secret-references). If missing at reconcile time, the resource reports `Ready=False` (`PasswordSecretMissing`) and retries. |
| `defaultDatabase` | string | No |   | `DEFAULT_DATABASE` for the login. Omitted from `CREATE LOGIN` when unset. |
| `checkPolicy` | bool | No | `true` | Sets `CHECK_POLICY` on `CREATE LOGIN`. |
| `checkExpiration` | bool | No | `false` | Sets `CHECK_EXPIRATION` on `CREATE LOGIN`. |

### expiration

`spec.expiration` (`ExpirationSpec`). Requires exactly one of `expiresAt` or `ttl`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `expiresAt` | string | Conditional |   | Absolute RFC 3339 timestamp. Must parse as a round-trip date, and on create it must be in the future. Mutually inclusive-or with `ttl`. |
| `ttl` | string | Conditional |   | Relative lifetime from `metadata.creationTimestamp`. Format: positive integer plus suffix `m` (minutes), `h` (hours), or `d` (days), e.g. `12h`, `30d`. |
| `action` | string (enum) | No | `Disable` | What happens at expiry. Allowed (case-insensitive): `Disable` (disable the login) or `Delete` (drop database users and the login). |
| `terminateSessions` | bool | No | `true` | When expiring, kill the login's active sessions (excluding the operator's own connection). |

### databasePermissions

`spec.databasePermissions[]` (`DatabasePermissionSpec`). Database users are created and maintained only on the target's primary connection.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `databases` | string[] | No |   | Explicit database names to map the user into. |
| `databaseFilter` | object | No |   | Pattern and label-based database selection. See [databaseFilter](#databasefilter). Combined with `databases` to compute the effective set. |
| `roles` | string[] | No |   | Database roles to add the user to (e.g. `db_datareader`, `db_datawriter`). |
| `schemaPermissions` | object[] | No |   | Schema-scoped grants. See [schemaPermissions](#schemapermissions). |

#### databaseFilter

`databasePermissions[].databaseFilter` (`DatabaseFilterSpec`).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `pattern` | string | No |   | Glob pattern matched against database names. |
| `exclude` | string[] | No |   | Database names to exclude from the matched set. |
| `matchLabels` | map[string]string | No |   | Label-based selection of databases. |

#### schemaPermissions

`databasePermissions[].schemaPermissions[]` (`SchemaPermissionSpec`).

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `schema` | string | No | `dbo` | Target schema. |
| `permissions` | string[] | No | `[]` | Permissions to `GRANT ... ON SCHEMA::`. Validated against an allow-list before execution. |

### overrides

`spec.overrides[]` (`TargetOverride`). Each override must specify **exactly one** of `instance` or `availabilityGroup` (enforced by the webhook). For a matched target, the override's `databasePermissions`, `serverRoles`, and `serverPermissions` fully replace the corresponding top-level lists.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `instance` | string | Conditional |   | Name of the `SqlInstance` this override applies to. Exactly one of `instance` or `availabilityGroup`. |
| `availabilityGroup` | string | Conditional |   | Name of the `SqlAvailabilityGroup` this override applies to. |
| `databasePermissions` | object[] | No |   | Replaces top-level `databasePermissions` for this target. Same shape as [databasePermissions](#databasepermissions). |
| `serverRoles` | string[] | No |   | Replaces top-level `serverRoles` for this target. |
| `serverPermissions` | string[] | No |   | Replaces top-level `serverPermissions` for this target. |

### secret references

`SecretReference`, used by `login.passwordSecretRef`.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | string | Yes | `""` | Name of the Secret in the same namespace as the `SqlUser`. |
| `key` | string | Yes | `""` | Key within the Secret holding the password value. |

## Status

`status` is a subresource maintained by the operator. It is a slim, etcd-light status: a convergence signal plus O(1) roll-up scalars. The former per-target `targetStatuses[]` array — and its nested `replicaStatuses[]`, `databaseUsers[]`, and `roles[]` trees — has been removed.

| Field | Type | Description |
|-------|------|-------------|
| `phase` | string | High-level lifecycle phase: `Pending` (initial), `Ready`, `Expired`, or `Failed`. |
| `observedGeneration` | integer (int64) | `metadata.generation` last reconciled by the operator. |
| `effectiveExpiresAt` | string | Computed absolute expiry (from `expiresAt`, or `ttl` plus creation time), ISO-8601. |
| `expired` | bool | True once the effective expiry has passed. Cleared on reactivation. |
| `revokedAt` | datetime | First time revocation began across targets. Cleared on reactivation. |
| `totalTargets` | integer (int32) | Targets (instances/AGs) matched by the selector. |
| `readyTargets` | integer (int32) | Targets fully provisioned this pass. |
| `failedTargets` | integer (int32) | Targets that failed provisioning this pass. |
| `conditions` | object[] | Standard conditions. Types emitted: `Ready`, `Expired`, `RevocationPending`. See below. |

`kubectl get sqluser` shows: `PHASE` (`.status.phase`), `READY` (`.status.readyTargets`), `TARGETS` (`.status.totalTargets`), `EXPIRED` (`.status.expired`), and `AGE`.

**Live detail.** Per-target login state, per-replica login/SID detail (for AG targets), and per-database user/role detail are no longer stored on the resource. Instead:

- They are served live via the `view.mssql.operator` aggregated read API — `kubectl get sqlusertargets`.
- Provisioning, disabling, and revocation occurrences are emitted as Kubernetes Events (`kubectl describe sqluser <name>`).
- Counts over time are exposed as Prometheus metrics (`mssqloperator_targets_*`).

`conditions[]` (`ConditionStatus`):

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Condition type (`Ready`, `Expired`, `RevocationPending`). |
| `status` | string | `True` / `False`. |
| `reason` | string | Machine-readable reason, e.g. `UserConfigured`, `PasswordSecretMissing`, `Reactivated`. |
| `message` | string | Human-readable detail. |
| `lastTransitionTime` | datetime | Last time the condition changed. |

## Validation & behaviour

**Admission webhook** (rejections return HTTP 422):

- `target` must specify at least one of `matchLabels`, `instances`, or `availabilityGroups`.
- `login` is required and `login.name` must be non-empty.
- Each `overrides[]` entry must set **exactly one** of `instance` or `availabilityGroup`.
- If `expiration` is present: `action` must be `Disable` or `Delete`, at least one of `expiresAt` or `ttl` is required, `expiresAt` must be a valid RFC 3339 timestamp (and on create, in the future), and `ttl` must be a positive integer with suffix `m`, `h`, or `d`.

**Reconcile behaviour:**

- **Target resolution.** The selector resolves to concrete `SqlInstance` or `SqlAvailabilityGroup` targets. For an AG, the login is provisioned on every replica connection. A standalone instance has a single connection.
- **Login provisioning.** If the login is absent, it is created with `CHECK_POLICY`, `CHECK_EXPIRATION`, and `DEFAULT_DATABASE` from the spec. For AG targets, the SID captured from the first replica is reused on the others to keep the login SID consistent.
- **Password rotation.** If the login already exists, its password is re-applied from the referenced Secret on every reconcile (`ALTER LOGIN`), so rotating the Secret value rotates the SQL password. A missing password Secret yields `Ready=False` (`PasswordSecretMissing`) and a retry rather than a hard failure.
- **Server roles/permissions.** Added on every connection (override list, else top-level list).
- **Database users.** Created and maintained only on the target's primary connection. The effective database set is `databases` combined with `databaseFilter`. A database user (named after the login) is created `FOR LOGIN` if missing, then role memberships and schema grants are applied. Role grants and schema and server permission grants are idempotent by intent, so they re-run each reconcile.
- **Per-target unreachability.** A target that cannot be provisioned emits a `TargetUnreachable` warning event and is skipped. Other targets still proceed.
- **Expiration.** When the effective expiry passes, the resource is marked `Expired`. With `action: Disable` the login is disabled (and, if `terminateSessions`, its sessions are killed). With `action: Delete` the database users and login are dropped. If a drop fails due to schema ownership, the operator transfers owned schemas to `dbo` and retries the drop. Revocation that cannot complete sets `RevocationPending` and is retried (~30s).
- **Reactivation.** If the expiry is later pushed into the future, the operator clears `Expired`/`RevokedAt`, re-enables previously disabled logins, and emits `UserReactivated`.
- **Deletion (finalizer).** On delete, the finalizer kills the login's sessions, drops every matching database user (transferring schema ownership to `dbo` first if a drop fails), and drops the login on each resolved target. Failures are retried up to 3 attempts per target. The `mssql.operator/force-finalize: "true"` annotation forces finalization to succeed despite errors.

## Related

- [`SqlInstance`](./sqlinstance.md): the instances targeted by a `SqlUser`.
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md): AG targets where logins are provisioned across replicas.
