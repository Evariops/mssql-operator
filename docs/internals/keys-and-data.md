# Keys and data internals

How key material and SQL-side state are kept. Usage:
[Backups and encryption guide](../guides/backups-and-encryption.md).

## Key material never touches the node's disk

- The SMK backups and TDE exports are written by SQL Server into an in-memory volume mounted at
  `/var/opt/mssqlop-keys`, read back by the operator, stored in Secrets, then deleted. On a pod that
  has this volume but where SQL Server does not see it yet, the backup is refused rather than written
  to `/tmp` on the node.
- Key Secrets are **immutable** and have **no owner reference**: they cannot be overwritten, and they
  outlive the resource. A deleted one is recreated within seconds from SQL Server.
- SMK backups are versioned (up to 10) and retaken when the SMK changes (`sys.symmetric_keys.modify_date`).
- The fingerprint of the machine key the data was created with is kept in the control record: a
  replaced machine key is detected (`MachineKeyChanged`) instead of silently making the data unreadable.
- Regenerating the SMK, or generating a machine key over existing data, is never automatic.

## TDE

- The DMK is protected by a dedicated password (`masterKeyPasswordSecretRef` on an AG, a generated
  `<instance>-master-key-password` on an instance), never by `sa`. A DMK still protected by `sa`
  (created by an old version) gets the new password added, verified, then the `sa` one removed.
- The S3 bundle is self-contained: certificate, private key and the key's password, encrypted
  (AES key or envelope), bound to its logical name, version and object key.
- Certificates are never dropped: a backup taken under an old certificate needs it to restore.
- Encryption scans are throttled per server (`maxConcurrentEncryptionScans`), read from
  `encryption_state`.

## Ownership registry in msdb

Each server keeps, in the schema `msdb.mssqlop`, what the operator owns there:

| Record | Used for |
|---|---|
| owned principals | which `SqlUser` owns each login, and the server rights it granted |
| original settings | the `sp_configure` values before a `SqlConfiguration` changed them |
| managed trace flags | the trace flags a configuration turned on |
| epoch stamps | the epoch of the last operator write (safety level 1) |
| watchdog state | what the quorum watchdog last decided (safety level 2) |

Database users carry extended properties (`mssqlop.owner`, `mssqlop.grants`) with what was granted in
that database. Because this state lives in SQL Server, a resource deleted and re-created by GitOps (new
UID, empty status) still knows what to restore or revoke.

## Log chain

The operator's log backups go to `/var/opt/mssql/backup/mssql-operator/` on the replica that takes
them (the primary). msdb records every backup per server; `kubectl get logchains` merges them across
replicas so a restore can be planned from one listing.
