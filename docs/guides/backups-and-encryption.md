# Backups and encryption

What the operator backs up, what you must back up yourself, how TDE works, and which keys to keep
safe outside the cluster.

## Database backups are yours

The operator does **not** schedule full or differential backups. It takes only:

- the **initialization backup** of a database it adds to an availability group (when the database has
  no backup yet), optionally in a daily window (`initialization.backupWindow`);
- **log backups** when nothing else backs the log up (below).

Schedule your own backups, for example with a [`SqlAgentJob`](../reference/sqlagentjob.md) running
your backup scripts, or an external tool. With a network policy, add an egress rule to your backup
target (`networkPolicy.egress`, for example the object storage of `BACKUP TO URL`). On an availability
group, `backupPreference` and per-replica `backupPriority` tell your scripts where to run.

## Log backups

A database in an AG is in FULL recovery: its log grows until it is backed up. To keep it from filling
the volume, each `SqlAvailabilityGroupDatabase` has a `logBackup.mode`:

| Mode | Behaviour |
|---|---|
| `Local` | When the log is 50 % full **and** nothing else truncated it, the operator backs it up (compressed) to `/var/opt/mssql/backup/mssql-operator/` on the primary. If your tooling backs up the log often enough, the operator never acts. |
| `Disabled` | Never. Your tooling owns log backups. |
| unset | Like `Local`, with the files deleted after 24 h, and the condition `LogBackupModeDeprecated`: this default will change, choose a mode. |

The operator's log backups are part of your restore chain. In `Local` mode they are kept until you
set `logBackup.retentionHours`; the condition `LogChainHeldByOperator` says when they are. Each one is an
event `LogBackedUp`. The whole chain recorded on each replica:

```bash
kubectl get logchains -n sql      # BYOPERATOR marks the operator's own files
```

If the log still fills (a long transaction, a lagging replica), the database gets `TransactionLogFull`
and `NeedsAttention`, with what holds the log in the message.

## Transparent data encryption

A [`SqlTdeCertificate`](../reference/sqltdecertificate.md) encrypts the selected databases. On every
server it targets (every replica of an AG) the operator:

1. creates the database master key in `master` if missing,
2. installs the certificate: generated (`OperatorManaged`) or imported from Secrets (`KubernetesSecret`),
3. saves the certificate and its private key to a Secret (`<logicalName>-<version>`), and optionally to
   an immutable archive Secret and to S3 as an encrypted bundle that can be restored on its own,
4. encrypts the selected databases, a few at a time (`maxConcurrentEncryptionScans`).

Rotation creates a new certificate and re-protects the databases with it: on demand
(`mssql-operator.evariops.com/rotate: "true"`), on an interval (`trigger: ManualOrInterval`), or when a
new Secret appears (`OnSourceCandidate`). Old certificates are **never** dropped by the operator, since
older backups need them to restore. Follow encryption with `kubectl get tdedatabasestates -n sql`.

To restore an encrypted database on another server, you need the certificate and its private key: the
source Secret or the S3 bundle.

## Key material

Every SQL Server instance protects its secrets with a chain of keys. Losing one makes everything below
it unreadable, and **the data on the volumes cannot be recovered without them**:

```
machine key (Secret, mounted in the pod)
  └── service master key (SMK, in master)
        └── database master key (DMK, in master)
              └── TDE certificate
                    └── encryption key of each database
```

The operator keeps this material in Secrets next to the resource:

| Secret | Content |
|---|---|
| `<name>-machine-key` | the machine key (generated unless you provide `machineKeySecretRef`) |
| `<name>-smk-backup` (one per replica on an AG: `<name>-<n>-smk-backup`) | versioned backups of the SMK, refreshed when the SMK changes |
| `<name>-smk-backup-password` | the password of those backups |
| `<name>-master-key-password` | the DMK password of a standalone instance (an AG uses `masterKeyPasswordSecretRef`) |
| `<name>-sa-applied` | the current and 10 previous `sa` passwords |
| `<logicalName>-<version>` | each TDE certificate and its private key |

What you can rely on:

- These Secrets are **not deleted** with the resource, and their data cannot be overwritten
  (immutable): deleting and re-creating a resource keeps its data readable.
- A deleted key Secret is recreated within seconds.
- The DMK is never protected by the `sa` password, so rotating `sa` never breaks TDE.

What you must do:

- **Copy these Secrets outside the cluster** (Velero, an external secret store, an encrypted backup).
  They live in the cluster's etcd: losing the cluster loses them.
- Restoring a service master key is manual: [key material runbook](../runbooks/key-material.md).
- Decommissioning for good means deleting the PVCs **and then** these Secrets, in that order. The
  runbook has the commands.
