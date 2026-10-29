# SqlTdeCertificate

Transparent Data Encryption for the databases of the instances and availability groups it targets:
the database master key and the TDE certificate on every server (every replica of an AG), database
encryption, certificate backups and rotation.

Guide: [Backups and encryption](../guides/backups-and-encryption.md#transparent-data-encryption).

## Example

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlTdeCertificate
metadata:
  name: orders-tde
  namespace: sql
spec:
  target:
    availabilityGroups: [orders]
  certificate:
    logicalName: orders-tde
    source:
      type: OperatorManaged          # the operator generates the certificate
      operatorManaged:
        expiryDate: "2030-01-01"
  databases:
    selector:
      names: [Orders]                # or allUserDatabases: true
    policy: {}                       # AES_256, encrypt databases that are not yet encrypted
```

## Main fields

| Field | Default | What it does |
|---|---|---|
| `target` | required | `instances`, `availabilityGroups` and/or `matchLabels`, same namespace. |
| `certificate.logicalName` | required | Name used to build the certificate and Secret names. |
| `certificate.source.type` | `OperatorManaged` | `OperatorManaged`: generated, and saved to a Secret `<logicalName>-<version>`. `KubernetesSecret`: imported from Secrets holding `certificate.cer`, `private-key.pvk` and `password`. |
| `certificate.archiveToSecret` | | Also archive each certificate to an immutable Secret. |
| `certificate.backupToS3` | | Also upload an encrypted, self-contained bundle (certificate, key and its password) to S3-compatible storage. |
| `databases.selector` | required | `allUserDatabases`, `names`, `namePatterns`, with excludes. |
| `databases.policy.encryptionAlgorithm` | `AES_256` | `AES_128`, `AES_192`, `AES_256`. |
| `databases.policy.nonEncryptedDatabasePolicy` | `Encrypt` | What to do with a selected database that is not encrypted: `Encrypt`, `Skip`, `Warn`, `Fail`. |
| `databases.policy.maxConcurrentEncryptionScans` | `1` | Encryption scans running at once on one server. |
| `rotation` | none | `trigger` (`ManualOnly`, `ManualOrInterval`, `OnSourceCandidate`), `intervalDays` (`365`), `mode` (`ProtectorOnly` or `ProtectorAndDekRegenerate`). |

Rotate now: annotate the resource with `mssql-operator.evariops.com/rotate: "true"`.

## What the operator never does

- It never drops a certificate, not after a rotation, not when the resource is deleted: a backup taken
  while the certificate protected a database would become impossible to restore. Drop superseded
  certificates yourself once no backup in your retention needs them.
- It never protects the master key with the `sa` password: an AG uses its
  `masterKeyPasswordSecretRef`, an instance a generated password kept in `<instance>-master-key-password`.

## Status

```console
$ kubectl get sqltdecertificate -n sql
NAME         PHASE   ENCRYPTED   PENDING   AGE
orders-tde   Ready   1           0         30d
```

`status.rotationPhase` follows a rotation: `Idle`, `Preparing`, `Rotating`, `RetentionWaiting`, `Failed`.

Per-database encryption state on each replica, live: `kubectl get tdedatabasestates -n sql`.
`CertificateExpiry` and `CertificateAge` conditions warn before the certificate gets old.
