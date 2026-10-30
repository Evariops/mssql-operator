# Backups and encryption

An availability group replicates every change, including a `DROP TABLE` or a corruption, so it does not replace backups. With TDE, the data can only be restored together with the keys that protect it. This guide describes which backups the operator takes, how log backups and TDE work, which key material it stores, and what must be kept outside the cluster.

## Backups taken by the operator

The operator does not schedule full or differential backups. It takes two kinds of backups, both needed for availability groups to work:

- the initialization backup of a database it adds to an AG, when the database has no backup yet, since SQL Server requires a log chain to join a database. `initialization.backupWindow` restricts this backup to a daily window for large databases;
- log backups, when no other tool takes them (next section).

Regular backups have to be scheduled separately, either with a [`SqlAgentJob`](databases-and-access.md#agent-jobs) running backup scripts or with an external tool. On an AG, `backupPreference` and the per-replica `backupPriority` tell the scripts which replica should run them. When the SQL Server pods have a network policy, the backup target needs an egress rule in `networkPolicy.egress`, for example the object storage used by `BACKUP TO URL`.

## Log backups and the log chain

A database in an AG uses the FULL recovery model. Its transaction log grows until it is backed up, and once the log volume is full every write fails with error 9002. Each `SqlAvailabilityGroupDatabase`, and each `SqlDatabase` on an AG through `spec.availabilityGroup`, sets `logBackup.mode`:

- `Local`: when the log is 50 % full and no other backup truncated it, the operator takes a compressed log backup to `/var/opt/mssql/backup/mssql-operator/` on the primary. When other tooling backs up the log often enough, the operator never takes one.
- `Disabled`: the operator never backs up the log, and other tooling must do it.
- Not set: behaves like `Local` with files deleted after 24 hours, and raises `LogBackupModeDeprecated`, because this default will change.

The operator's log backups are part of the restore chain: a point-in-time restore needs every log backup since the last full backup, including these. In `Local` mode they are therefore kept until `logBackup.retentionHours` is set. The condition `LogChainHeldByOperator` indicates that part of the chain is in the operator's files, and each backup publishes the event `LogBackedUp`. The chain recorded in msdb on every replica is available through the view API:

```bash
kubectl get logchains -n sql      # BYOPERATOR marks the operator's own files
```

When the log fills anyway, for example because of a long transaction or a lagging replica, the database raises `TransactionLogFull` with `NeedsAttention`, and the message gives the value of `log_reuse_wait_desc`.

## Transparent data encryption

A [`SqlTdeCertificate`](../reference/resources.md#sqltdecertificate) encrypts the databases it selects. On every server it targets, which on an AG means every replica, the operator:

1. creates the database master key in `master` when it is missing, protected by its own password and never by `sa`;
2. installs the certificate, either generated (`OperatorManaged`) or imported from Secrets (`KubernetesSecret`);
3. stores the certificate and its private key in the Secret `<logicalName>-<version>`, and optionally in an immutable archive Secret and in S3 as an encrypted bundle that can be restored on its own;
4. encrypts the selected databases, limited to `maxConcurrentEncryptionScans` concurrent scans per server, since an encryption scan reads the whole database.

Certificates are copied between replicas without intermediate files.

A rotation creates a new certificate and re-protects the databases with it. It is triggered by the annotation `mssql-operator.evariops.com/rotate: "true"`, on an interval (`trigger: ManualOrInterval`), or when a new source Secret appears (`OnSourceCandidate`). The operator never drops old certificates, because a backup taken under a certificate needs that certificate to be restored. Superseded certificates must be dropped manually once no backup in the retention period depends on them.

Restoring an encrypted database on another server requires its certificate and private key, from the Secret or from the S3 bundle. `kubectl get tdedatabasestates -n sql` shows the encryption state of each database.

## Key material

SQL Server protects its secrets with a chain of keys. Each key protects the next one, and the loss of a key makes everything below it unreadable, even with intact volumes:

```
machine key (Secret, mounted in the pod)
  └── service master key (SMK, in master)
        └── database master key (DMK, in master)
              └── TDE certificate
                    └── encryption key of each database
```

SQL Server on Linux reads the machine key from a file at startup. In an unmanaged container, that file is regenerated when the pod is recreated, and the service master key can no longer be decrypted. The operator generates the machine key once, stores it in a Secret and mounts it in the pod.

The key material is stored in these Secrets, in the namespace of the resource:

| Secret | Content |
|---|---|
| `<name>-machine-key` | the machine key, generated unless `machineKeySecretRef` is set |
| `<name>-smk-backup` (on an AG, one per replica: `<name>-<n>-smk-backup`) | up to ten versioned SMK backups, refreshed when the SMK changes |
| `<name>-smk-backup-password` | the password of the SMK backups |
| `<name>-master-key-password` | the DMK password of a standalone instance (an AG uses `masterKeyPasswordSecretRef`) |
| `<name>-sa-applied` | the current and ten previous `sa` passwords, used during rotations |
| `<logicalName>-<version>` | each TDE certificate and its private key |

These Secrets are immutable, have no owner reference, and are not deleted with the resource, so a resource deleted and re-created keeps its data readable. A deleted key Secret is recreated from SQL Server at the next reconcile. A replaced machine key Secret is detected through the fingerprint kept in the control record, and raises `NeedsAttention` with reason `MachineKeyChanged`.

### Copies outside the cluster

All of this material is stored in etcd. If the cluster is lost without a copy of these Secrets, the data on the volumes cannot be decrypted. They should be copied outside the cluster, for example with Velero, an external secret store or an encrypted backup, and the copy should be tested.

## Restoring the service master key

After a restart, encrypted databases are inaccessible and the error log reports error 15466 (`An error occurred during decryption`): the machine key no longer matches the service master key.

The operator does not restore an SMK automatically, because `RESTORE SERVICE MASTER KEY` reads a file from the pod filesystem, and SQL Server on Linux cannot write that file from a session. The manual procedure is:

```bash
NS=sql; NAME=reporting; POD=reporting-0
BACKUP=${NAME}-smk-backup            # on an AG: <ag>-<n>-smk-backup, one per replica (NAME=<ag>, POD=<ag>-<n>)
SA=$(kubectl -n $NS get secret <sa-password-secret> -o jsonpath='{.data.password}' | base64 -d)

# 1. Get the latest SMK backup and its password. The Secret keeps up to 10 versions
#    (smk-<date>.bak); the annotation smk-latest-key names the latest one. If the latest is an
#    SMK regenerated by mistake, use the previous one (jq -r '.data | keys[]' lists them).
KEY=$(kubectl -n $NS get secret $BACKUP \
  -o jsonpath='{.metadata.annotations.mssql-operator\.evariops\.com/smk-latest-key}')
kubectl -n $NS get secret $BACKUP -o json | jq -r --arg k "$KEY" '.data[$k]' | base64 -d > smk.bak
PASSWORD=$(kubectl -n $NS get secret ${NAME}-smk-backup-password -o jsonpath='{.data.password}' | base64 -d)

# 2. Copy it into the pod.
kubectl -n $NS cp smk.bak $POD:/var/opt/mssql/data/smk.bak -c mssql

# 3. Restore, without FORCE.
kubectl -n $NS exec $POD -c mssql -- /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA" -C -Q \
  "RESTORE SERVICE MASTER KEY FROM FILE = '/var/opt/mssql/data/smk.bak' DECRYPTION BY PASSWORD = '$PASSWORD'"

# 4. Clean up.
kubectl -n $NS exec $POD -c mssql -- rm -f /var/opt/mssql/data/smk.bak
rm smk.bak
```

If the restore fails because the current key cannot be decrypted, do not retry with `FORCE`: it permanently destroys everything the current SMK protects. Restore a copy of the matching machine key Secret first, then retry.

## Decommissioning

The key Secrets are kept on purpose when a resource is deleted. Removing everything takes these commands:

```bash
kubectl -n $NS delete sqlinstance $NAME
kubectl -n $NS get pvc | grep $NAME                       # data-, log-, backup-<name>-0 …
kubectl -n $NS delete pvc data-${NAME}-0 log-${NAME}-0 backup-${NAME}-0
kubectl -n $NS delete secret ${NAME}-machine-key ${NAME}-smk-backup ${NAME}-smk-backup-password \
  ${NAME}-sa-applied ${NAME}-master-key-password
```

Delete the PVCs before the Secrets. In the opposite order, the data becomes unreadable before it is confirmed that it is no longer needed.
