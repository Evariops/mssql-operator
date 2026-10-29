# SqlUser

A login, its database users, their roles and permissions, on one or more instances or availability
groups. The password comes from a Secret; changing the Secret rotates it.

Guide: [Users and permissions](../guides/users.md).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlUser
metadata:
  name: orders-app
  namespace: sql
spec:
  target:
    availabilityGroups: [orders]       # or instances: [...], or matchLabels: {...}
  login:
    name: orders_app
    passwordSecretRef: { name: orders-app-password, key: password }
  databasePermissions:
    - databases: [Orders]
      roles: [db_datareader, db_datawriter]
  expiration:                          # optional: time-bounded access
    ttl: 30d
    action: Disable
```

## Main fields

| Field | Default | What it does |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, in the same namespace. On an AG, the login is created on every replica with the same SID. |
| `login.name` | required | Login name; also the name of the database users. |
| `login.passwordSecretRef` | | Secret holding the password. |
| `login.defaultDatabase`, `checkPolicy`, `checkExpiration` | | `CREATE LOGIN` options. |
| `databasePermissions[]` | | `databases` (or a `databaseFilter` pattern), `roles`, and `schemaPermissions` grants. |
| `serverRoles` / `serverPermissions` | | Fixed server roles and server permissions (allow-listed). |
| `overrides[]` | | Different permissions for one `instance` or `availabilityGroup`. |
| `expiration` | | `expiresAt` (RFC 3339) or `ttl` (`12h`, `30d`); `action: Disable` or `Delete`; `terminateSessions` (default `true`). Pushing the date later reactivates the login. |

`sysadmin`, `securityadmin` and `serveradmin` are granted only if the target carries
`mssql-operator.evariops.com/allow-privileged-roles: "true"`.

## Ownership

Each server records which `SqlUser` owns a login (in `msdb.mssqlop`). A second `SqlUser` declaring the
same login is refused (`LoginOwnedByAnotherResource`). A `SqlUser` recreated by GitOps takes its login
back and revokes what its spec no longer grants.

## Status

```console
$ kubectl get sqluser -n sql
NAME         PHASE   READY   TARGETS   EXPIRED   AGE
orders-app   Ready   1       1         false     5d
```

- `status.phase`: `Pending`, `Ready`, `Expired`, `Failed`.
- Per-target and per-database detail, live from SQL Server: `kubectl get sqlusertargets -n sql`.

Deleting the resource drops the database users and the login on every target (sessions are killed
first). `mssql-operator.evariops.com/force-finalize: "true"` lets the deletion finish when a target
is gone for good.
