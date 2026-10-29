# Users and permissions

Logins and database users are declared with [`SqlUser`](../reference/sqluser.md): one resource per
login, on one or more instances or availability groups.

## An application login

```yaml
apiVersion: v1
kind: Secret
metadata: { name: orders-app-password, namespace: sql }
stringData:
  password: "<a strong password>"
---
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlUser
metadata: { name: orders-app, namespace: sql }
spec:
  target:
    availabilityGroups: [orders]
  login:
    name: orders_app
    passwordSecretRef: { name: orders-app-password, key: password }
  databasePermissions:
    - databases: [Orders]
      roles: [db_datareader, db_datawriter]
      schemaPermissions:
        - schema: reporting
          permissions: [SELECT]
```

On an availability group the login is created on every replica with the same SID, so the database
user keeps working after a failover. The database user is created on the primary (the AG replicates it).

## Change the password

Update the Secret. The operator applies the new password on every target at its next pass.

## Temporary access

```yaml
spec:
  expiration:
    ttl: 8h                 # or expiresAt: "2026-11-30T18:00:00Z"
    action: Disable         # or Delete (drops the users and the login)
    terminateSessions: true
```

At expiry the login is disabled (or dropped) and its sessions killed. Pushing `expiresAt` later
re-enables it.

## Server-level rights

`serverRoles` takes fixed server roles, `serverPermissions` server permissions from an allow-list.
`sysadmin`, `securityadmin` and `serveradmin` amount to owning the server: they are granted only when
the target resource carries `mssql-operator.evariops.com/allow-privileged-roles: "true"`. Without it
they are skipped and the `SqlUser` reports `PrivilegedRolesDenied`.

## Different rights on one target

```yaml
spec:
  overrides:
    - availabilityGroup: orders-staging
      databasePermissions:
        - databases: [Orders]
          roles: [db_owner]
```

An override replaces the top-level lists for that target only.

## Check what is applied

```bash
kubectl get sqluser -n sql            # targets ready / total
kubectl get sqlusertargets -n sql     # the login and its database users on each target, live
```

## Ownership and deletion

Each server records which `SqlUser` owns each login. Two `SqlUser` resources cannot manage the same
login (`LoginOwnedByAnotherResource`); a `SqlUser` re-created by GitOps takes its login back. An
existing login that no `SqlUser` owns yet is taken over by the first one that declares it. Deleting
the `SqlUser` kills the login's sessions, drops its database users and drops the login on every target.
