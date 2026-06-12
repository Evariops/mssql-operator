# `SqlTdeCertificate`

> Manages Transparent Data Encryption (TDE) certificates and the database master key for one or more targeted SQL Server instances or Availability Groups, and drives database encryption and protector rotation.

`apiVersion: mssql-operator.evariops.com/v1alpha1` · `kind: SqlTdeCertificate` · scope: Namespaced · plural: `sqltdecertificates`

## Minimal example

The operator generates and manages the certificate. No password Secret is required. When `sourceSecretEncryptionPasswordRef` is omitted, the operator generates a strong random password and persists it in the source Secret.

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlTdeCertificate
metadata:
  name: app-tde
  namespace: sql
spec:
  target:
    instances:
      - app-sql
  certificate:
    logicalName: app-tde
    source:
      type: OperatorManaged
      operatorManaged:
        expiryDate: "2030-01-01"
  databases:
    selector:
      allUserDatabases: true
    policy: {}
```

## Overview

A `SqlTdeCertificate` provisions a TDE certificate (and the `master` database master key) on the targeted SQL Server instances/replicas, then encrypts the selected user databases with it. The operator can generate and manage the certificate (`OperatorManaged`), or import existing certificate material stored in a Kubernetes Secret (`KubernetesSecret`). It optionally archives the certificate to an immutable Kubernetes Secret and/or backs it up to S3. It also supports controlled protector rotation (with optional DEK regeneration) gated by backup evidence and a retirement waiting period.

## Spec

`SqlTdeCertificate.spec` has four top-level fields.

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | object (`TargetSelector`) | Yes | `{}` | Selects the instances or Availability Groups to deploy the certificate on. See [`target`](#target). |
| `certificate` | object | Yes | `{}` | Certificate identity, source and backup configuration. See [`certificate`](#certificate). |
| `databases` | object | Yes | `{}` | Which databases to encrypt and the encryption policy. See [`databases`](#databases). |
| `rotation` | object | No |  | Optional protector-rotation policy. When omitted, the operator only provisions/encrypts (no rotation triggering). See [`rotation`](#rotation). |

### `target`

At least one selector field must be provided (validated by the shared target validator).

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target.matchLabels` | map<string,string> | No | `null` | Label selector matching target resources. |
| `target.instances` | string[] | No | `null` | Explicit `SqlInstance` names to target. |
| `target.availabilityGroups` | string[] | No | `null` | Explicit `SqlAvailabilityGroup` names to target. |

### `certificate`

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `certificate.logicalName` | string | Yes | `""` | Operator-internal logical name for the certificate, used in name templates. Must be non-empty. |
| `certificate.source` | object | Yes | `{}` | How the certificate material is obtained. See [`certificate.source`](#certificatesource). |
| `certificate.archiveToSecret` | object | No | `null` | Archive the certificate + private key to a Kubernetes Secret. See [`certificate.archiveToSecret`](#certificatearchivetosecret). |
| `certificate.backupToS3` | object | No | `null` | Back the certificate up to S3-compatible object storage. See [`certificate.backupToS3`](#certificatebackuptos3). |

#### `certificate.source`

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `certificate.source.type` | string (enum) | Yes | `OperatorManaged` | One of `OperatorManaged` or `KubernetesSecret`. |
| `certificate.source.operatorManaged` | object | Required when `type=OperatorManaged` | `null` | Operator-generated certificate settings. |
| `certificate.source.kubernetesSecret` | object | Required when `type=KubernetesSecret` | `null` | Import settings discovering certificate Secrets. |

`certificate.source.operatorManaged`:

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `subjectTemplate` | string | No | `TDE certificate {logicalName}` | Certificate subject. Supports `{logicalName}` and `{version}` placeholders. |
| `expiryDate` | string | Yes (when `type=OperatorManaged`) | `""` | Certificate expiry date. Must be non-empty. |
| `keyLength` | integer (int32) | No | `2048` | RSA key length. |
| `sqlCertificateNameTemplate` | string | No | `{logicalName}_{version}` | Template for the in-SQL certificate name. |
| `sourceSecretNameTemplate` | string | No | `{logicalName}-{version}` | Template for the operator-written source Secret holding the cert/key/password. |
| `sourceSecretEncryptionPasswordRef` | object (`SecretReference`) | No | `{}` | Secret holding the private-key encryption password used to export the cert. If unset, the operator generates and persists a strong random password in the source Secret. |

`certificate.source.kubernetesSecret`:

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `activationPolicy` | string | No | `SecretRole` | How the active version is chosen: `SecretRole` (uses the `mssql-operator.evariops.com/certificate-role` label, preferring `candidate` then `active`) or `NewestReadyCandidate` (newest Secret labelled `mssql-operator.evariops.com/ready=true`). |
| `discovery` | object | Yes | `{}` | How candidate Secrets are found. |
| `discovery.namespace` | string | No | `null` | Namespace to search. Defaults to the CR namespace. |
| `discovery.names` | object[] (`CertificateSecretVersionRef`) | Conditional | `null` | Explicit list of Secret references. Mutually exclusive with `matchLabels`. |
| `discovery.matchLabels` | map<string,string> | Conditional | `null` | Label selector for discovering Secrets. Mutually exclusive with `names`. |

`discovery.names[]` (`CertificateSecretVersionRef`):

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `secretName` | string | Yes | `""` | Name of the Secret holding the certificate material. |
| `version` | string | No | `null` | Optional explicit version label. |
| `sqlCertificateName` | string | No | `null` | Override the in-SQL certificate name. |
| `certificateKey` | string | No | `certificate.cer` | Secret data key holding the certificate (`.cer`). |
| `privateKeyKey` | string | No | `private-key.pvk` | Secret data key holding the private key (`.pvk`). |
| `passwordKey` | string | No | `password` | Secret data key holding the private-key password. |

> Note: at least one of `discovery.names` or `discovery.matchLabels` is required when `type=KubernetesSecret`, and they cannot both be set.

#### `certificate.archiveToSecret`

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `secretNameTemplate` | string | No | `{logicalName}-{thumbprintShort}-{timestamp}` | Template for the archive Secret name. Supports `{logicalName}`, `{version}`, `{thumbprintShort}`, `{timestamp}`. |
| `encryptionPasswordSecretRef` | object (`SecretReference`) | No | `{}` | Secret holding the password used to encrypt the archived private key. Falls back to the operator-resolved password if absent. |
| `immutable` | boolean | No | `true` | Mark the created archive Secret as immutable. |

#### `certificate.backupToS3`

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `connection` | object | Yes | `{}` | S3 connection settings. |
| `connection.endpoint` | string | Yes (when `backupToS3` set) | `""` | S3 endpoint URL. |
| `connection.bucket` | string | Yes (when `backupToS3` set) | `""` | Target bucket. |
| `connection.region` | string | No | `us-east-1` | S3 region. |
| `connection.clusterName` | string | Yes (when `backupToS3` set) | `""` | Cluster name, used in the object-key template. |
| `connection.credentialsSecretRef` | object (`SecretReference`) | Yes (`.name` required) | `{}` | Secret holding `accessKeyId` and `secretAccessKey`. |
| `connection.forcePathStyle` | boolean | No | `false` | Use path-style addressing. |
| `objectKeyTemplate` | string | No | `{clusterName}/tde/{logicalName}/{version}/{thumbprintShort}.enc` | Template for the S3 object key. |
| `encryption` | object | Yes | `{}` | Backup-encryption settings. |
| `encryption.type` | string (enum) | Yes | `AesKey` | One of `AesKey` or `EnvelopeEncryption`. |
| `encryption.aesKey.keySecretRef` | object (`SecretReference`) | Required when `type=AesKey` (`.name`) | `{}` | Secret holding the symmetric AES key. |
| `encryption.envelopeEncryption.publicKeySecretRef` | object (`SecretReference`) | Required when `type=EnvelopeEncryption` (`.name`) | `{}` | Secret holding the public key used for envelope encryption. |
| `encryption.envelopeEncryption.privateKeySecretRef` | object (`SecretReference`) | No | `null` | Optional Secret holding the private key (for restore). |

### `databases`

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `databases.selector` | object | Yes | `{}` | Which databases to encrypt. |
| `databases.policy` | object | Yes | `{}` | Encryption policy. |

`databases.selector`:

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `allUserDatabases` | boolean | No | `false` | Select all user databases. |
| `names` | string[] | No | `null` | Explicit database names. |
| `namePatterns` | string[] | No | `null` | Glob/pattern matches for database names. |
| `excludeNames` | string[] | No | `null` | Explicit names to exclude. |
| `excludePatterns` | string[] | No | `null` | Patterns to exclude. |
| `excludeSystemDatabases` | boolean | No | `true` | Exclude system databases. |
| `missingDatabasePolicy` | string (enum) | No | `Warn` | Behaviour for explicitly named databases not found on the target: `Ignore`, `Warn`, or `Fail`. |

> Note: the selector must specify at least one of `allUserDatabases`, `names`, or `namePatterns`.

`databases.policy`:

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `encryptionAlgorithm` | string (enum) | No | `AES_256` | One of `AES_128`, `AES_192`, `AES_256`, `TRIPLE_DES_3KEY`. |
| `nonEncryptedDatabasePolicy` | string (enum) | No | `Encrypt` | Behaviour for databases that have no DEK: `Encrypt`, `Skip`, `Warn`, or `Fail`. |

### `rotation`

Optional. When omitted, the operator provisions and encrypts but does not perform a rotation pass.

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `intervalDays` | integer (int32) | No | `365` | Rotation interval. Must be `>= 1` when `trigger=ManualOrInterval`. |
| `alertAfterCertificateAgeDays` | integer (int32) | No | `730` | Age (days) after which a `CertificateAge` warning condition is raised. |
| `mode` | string (enum) | No | `ProtectorOnly` | One of `ProtectorOnly` (re-key the protector only) or `ProtectorAndDekRegenerate` (also regenerate the DEK). |
| `trigger` | string (enum) | No | `ManualOnly` | One of `ManualOnly`, `ManualOrInterval`, `OnSourceCandidate`. |
| `maxParallelTargets` | integer (int32) | No | `1` | Max targets processed per reconcile. Must be `>= 1`. |
| `dekRegeneration` | object | Required when `mode=ProtectorAndDekRegenerate` | `null` | DEK-regeneration throttling. |
| `dekRegeneration.maxParallelDatabasesPerTarget` | integer (int32) | No | `1` | Max databases regenerated per target per reconcile. Must be `>= 1`. |
| `dekRegeneration.perDatabaseTimeoutMinutes` | integer (int32) | No | `120` | Per-database DEK regeneration timeout. |
| `retirement` | object | No | `{}` | Old-certificate retirement policy. |
| `retirement.notBeforeDays` | integer (int32) | No | `90` | Days to wait before the old certificate may be retired. |
| `retirement.approvalMode` | string (enum) | No | `Manual` | One of `Manual` (requires an approval annotation) or `AutomaticWhenSafe`. |
| `retirement.requireFreshFullBackupAfterRotation` | boolean | No | `true` | Block retirement until verified backup evidence exists. |
| `retirement.requireLogBackupAfterDekRegeneration` | boolean | No | `true` | Require a log backup after DEK regeneration. |

## Status

`status` is populated by the controller. It is a slim, etcd-light status: a convergence signal plus O(1) roll-up scalars. The former per-target `targetStatuses[]` tree — rotation `history[]`, `evidences[]`, `databaseSelection.databases[]`, per-replica `replicaStatuses[]`, and per-target `activeVersion`/`candidateVersion` — has been removed because it grew past etcd's per-object size limit.

| Field | Type | Description |
| --- | --- | --- |
| `phase` | string | High-level lifecycle phase (default `Pending`). |
| `observedGeneration` | integer (int64) | `metadata.generation` last reconciled by the operator. |
| `targets` | integer (int32) | Targets (instances/AGs) matched by the selector. |
| `activeVersion` | string | Active certificate version currently protecting databases. |
| `activeThumbprint` | string | Thumbprint of the active certificate. |
| `candidateThumbprint` | string | Candidate (in-rotation) certificate thumbprint, when a rotation is in flight. |
| `rotationPhase` | string | Coarse rotation phase: `Idle`, `Preparing`, `Rotating`, `RetentionWaiting`, or `Failed`. |
| `lastRotationTime` | date-time | When the last rotation completed. |
| `retireNotBefore` | date-time | Earliest time the old certificate may be retired. |
| `retirementPendingApproval` | boolean | Old-cert retirement is waiting for manual approval. |
| `encryptedDatabases` | integer (int32) | Roll-up of databases encrypted across all targets. |
| `pendingDatabases` | integer (int32) | Roll-up of databases pending encryption/rotation across all targets. |
| `failedDatabases` | integer (int32) | Roll-up of databases that failed across all targets. |
| `conditions[]` | `ConditionStatus[]` | Standard conditions: `type`, `status`, `reason`, `message`, `lastTransitionTime`. The controller sets `Ready` (reasons: `NoMatch`, `NoSource`, `CertificateDeployed`, `PartialError`, `Failed`), `CertificateAge`, and `CertificateExpiry`. |

`kubectl get sqltdecertificate` shows: `PHASE` (`.status.phase`), `ENCRYPTED` (`.status.encryptedDatabases`), `PENDING` (`.status.pendingDatabases`), `ROTATION` (`.status.rotationPhase`), and `AGE`.

**Live detail.** Per-target, per-database, and per-replica detail — plus rotation history and backup evidence — is no longer stored on the resource. Instead:

- Per-database encryption state and per-replica certificate/master-key state are served live via the `view.mssql-operator.evariops.com` aggregated read API — `kubectl get tdedatabasestates`.
- Rotation, encryption, and backup occurrences (and failures) are emitted as Kubernetes Events (`kubectl describe sqltdecertificate <name>`). Because the operator is stateless, rotation history is retained only as Events — there is no long-term history array on the resource.
- Counts over time are exposed as Prometheus metrics (`mssqloperator_tde_databases_*`, `mssqloperator_targets_*`).
- Backup evidence is the immutable Secret / S3 objects themselves (the source/archive Secrets and the S3 backup), not a status array.

## Validation & behaviour

### Admission webhook (Create / Update)

All failures return HTTP 422.

- `target` must be valid per the shared target selector validator.
- `certificate.logicalName` is required.
- `certificate.source.type` must be `OperatorManaged` or `KubernetesSecret`.
- If `OperatorManaged`: `operatorManaged` must be present and `operatorManaged.expiryDate` non-empty.
- If `KubernetesSecret`: `kubernetesSecret` must be present, and `discovery` must specify `names` or `matchLabels` (the two are mutually exclusive).
- If `backupToS3` is set: `connection.endpoint`, `connection.bucket`, `connection.clusterName`, and `connection.credentialsSecretRef.name` are all required. `encryption.type` must be `AesKey` or `EnvelopeEncryption`. For `AesKey`, the `aesKey.keySecretRef.name` is required. For `EnvelopeEncryption`, the `envelopeEncryption.publicKeySecretRef.name` is required.
- `databases.selector` must specify `allUserDatabases`, `names`, or `namePatterns`.
- `databases.selector.missingDatabasePolicy` must be `Ignore`, `Warn`, or `Fail`.
- `databases.policy.encryptionAlgorithm` must be `AES_128`, `AES_192`, `AES_256`, or `TRIPLE_DES_3KEY`.
- `databases.policy.nonEncryptedDatabasePolicy` must be `Encrypt`, `Skip`, `Warn`, or `Fail`.
- If `rotation` is set: `mode` must be `ProtectorOnly` or `ProtectorAndDekRegenerate`. `trigger` must be `ManualOnly`, `ManualOrInterval`, or `OnSourceCandidate`. `maxParallelTargets >= 1`. `intervalDays >= 1` when `trigger=ManualOrInterval`. `dekRegeneration` is required when `mode=ProtectorAndDekRegenerate`, and its `maxParallelDatabasesPerTarget >= 1`. `retirement.approvalMode` must be `Manual` or `AutomaticWhenSafe`.

### Reconcile behaviour

- **Target resolution.** The selector is resolved to concrete targets. If none match, `Ready=False` (`NoMatch`) and reconcile ends.
- **Version resolution.** For `OperatorManaged`, a daily version label (`yyyyMMdd`) is derived. If the source Secret already exists, the existing material is reused, otherwise the certificate is flagged for generation. For `KubernetesSecret`, candidate Secrets are discovered (by names or labels) and the active one chosen per `activationPolicy`. If no source can be resolved, `Ready=False` (`NoSource`).
- **Master key & certificate install.** On each target/replica the `master` database master key is created if missing, then the certificate is created (generate) or restored from password-protected `.cer`/`.pvk` material (import). Imported certificates require a private-key password. For AG targets the certificate is exported with the resolved password on replica 0 and restored on every other replica before any database operation. Per-replica certificate/master-key state is no longer persisted on the resource; it is served live via the `view.mssql-operator.evariops.com` API (`kubectl get tdedatabasestates`) and surfaced as Events.
- **Source Secret.** When the operator generates a certificate it persists `certificate.cer`, `private-key.pvk`, and `password` into a Kubernetes Secret labelled `mssql-operator.evariops.com/*` and annotated `argocd.argoproj.io/sync-options: Delete=false`. The export password comes from `sourceSecretEncryptionPasswordRef`, the password already stored in the source Secret, or a freshly generated random password.
- **Backup / evidence gating.** Before a rotation is allowed, backup evidence is verified. The evidence is the immutable Secret / S3 objects themselves (no longer a status array), and verification occurrences are emitted as Events. The source Secret must exist. If `archiveToSecret` is set, an (idempotent, optionally immutable) archive Secret is created. If `backupToS3` is set, the cert bundle is encrypted (AES key or envelope) and uploaded (idempotent via HEAD check) with a recorded SHA-256. Missing or failed evidence blocks rotation.
- **Rotation triggering.** With no `rotation` block the operator provisions/encrypts only. Otherwise, `ManualOnly` requires the `mssql-operator.evariops.com/rotate: "true"` annotation, `ManualOrInterval` triggers on that annotation or after `intervalDays` since the last completion, and `OnSourceCandidate` triggers when the resolved version differs from the active version.
- **Database processing.** Selected databases are snapshotted. Databases with no DEK follow `nonEncryptedDatabasePolicy` (`Encrypt` enables TDE, `Skip`/`Warn` skip, `Fail` errors). Encrypted databases have their protector changed to the new certificate when the thumbprint differs. With `mode=ProtectorAndDekRegenerate` the DEK is regenerated subject to `maxParallelDatabasesPerTarget` (excess go to `Pending` for a later reconcile) and `perDatabaseTimeoutMinutes`. A SQL "log backup" error moves the database to `WaitingForLogBackup`.
- **Promotion & retirement.** When all selected databases complete, the candidate is promoted to active, the rotation enters `RetentionWaiting`, and `retireNotBefore` is set to now + `notBeforeDays`. Old-cert retirement waits for that deadline and requires verified backup evidence when `requireFreshFullBackupAfterRotation` is set. Under `approvalMode: Manual` it also requires the `mssql-operator.evariops.com/approve-retirement` annotation before completing.
- **Finalizer.** The finalizer deliberately does **not** drop certificates on CR deletion. Certificate backups are preserved so encrypted databases remain recoverable.

## More examples

### Import an existing certificate from a Kubernetes Secret

Instead of letting the operator generate the certificate, import existing `.cer`/`.pvk` material discovered from a Secret. The referenced Secret must hold the certificate, the private key, and the private-key password.

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlTdeCertificate
metadata:
  name: app-tde
  namespace: sql
spec:
  target:
    instances:
      - app-sql
  certificate:
    logicalName: app-tde
    source:
      type: KubernetesSecret
      kubernetesSecret:
        discovery:
          names:
            - secretName: app-tde-material
  databases:
    selector:
      allUserDatabases: true
    policy: {}
```

## Related

- [`SqlInstance`](./sqlinstance.md)
- [`SqlAvailabilityGroup`](./sqlavailabilitygroup.md)
- [`SqlAvailabilityGroupDatabase`](./sqlavailabilitygroupdatabase.md)
