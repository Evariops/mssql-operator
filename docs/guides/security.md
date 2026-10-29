# Security

What is protected by default, and what to tighten for production.

## Tenancy: the namespace is the boundary

- A `SqlUser`, `SqlConfiguration`, `SqlAgentJob`, `SqlStoredProcedure` or `SqlTdeCertificate` only
  targets instances and AGs of its own namespace. Whoever can create these resources in a namespace
  controls the SQL Servers of that namespace.
- Run one instance or AG per team, in the team's namespace. A shared server is one trust zone: a
  `SqlConfiguration` changes it for everyone, and broad database filters reveal other teams' database
  names through the view API.
- `sysadmin`, `securityadmin` and `serveradmin` are granted only on targets annotated
  `mssql-operator.evariops.com/allow-privileged-roles: "true"`; `deployAs: Sa` procedures only on
  targets annotated `allow-sa-deployments: "true"`.
- Bind the `view-reader` ClusterRole with namespaced RoleBindings, so each team reads its own namespace.

## Admission webhook

Keep `webhook.enabled: true` and `failurePolicy: Fail`: every resource is validated before it is
stored (unsafe security contexts, images outside `operator.allowedImageRegistries`, dangerous
`sp_configure` options, downgrades…). The most sensitive rules are checked again when applied, so they
hold even without the webhook.

## Encrypt and authenticate connections

**Clients → SQL Server.** Give the servers a certificate (a `kubernetes.io/tls` Secret, cert-manager
works) and optionally refuse unencrypted clients:

```yaml
spec:                        # SqlInstance; under instanceTemplate on a SqlAvailabilityGroup
  tls:
    secretName: sql-tls
    forceEncryption: true
```

SANs: `<instance>.<namespace>.svc.cluster.local` for an instance;
`*.<ag>-headless.<namespace>.svc.cluster.local` and the listener Services for an AG. SQL Server reads
the certificate at startup: a renewed one is used from the next restart.

**Operator → SQL Server.** By default the connection is encrypted but the server certificate is not
verified (`sqlConnection.trustServerCertificate: true`), so a pod on the path could capture the `sa`
password. Once every server has a certificate:

```yaml
sqlConnection:
  encrypt: Strict                # SQL Server 2022+; Mandatory also validates on 2019
  trustServerCertificate: false
  caSecretName: sql-ca           # Secret in the operator namespace, CA in ca.crt
```

## Network policies

- **Operator pods** (`networkPolicy.enabled`, on by default): egress only to DNS, the API server,
  SQL Server (1433, 5022), the other operator pods and, if `allowExternalHttps`, HTTPS for GitHub and
  S3. Restrict who scrapes metrics with `metricsFrom`, and who calls the webhook with `webhookFrom` or
  `webhookFromApiServerOnly`. Metrics are plain HTTP: never expose them outside the cluster.
- **SQL Server pods** (`spec.networkPolicy.enabled`, Cilium): only the same namespace,
  `allowedNamespaces` and the operator pods reach 1433. Nothing goes out except DNS, the other
  replicas, and the destinations you list in `networkPolicy.egress` (SMTP relay, backup storage,
  linked servers). The SQL pods have no access to the Kubernetes API.

## Operator privileges

- The operator reads Secrets in every namespace (passwords and keys of the resources it manages). A
  compromised operator can read them all: treat its namespace and ServiceAccount as highly privileged.
- The breakout controller can taint nodes and delete VolumeAttachments. It runs with its own
  ServiceAccount (`breakout.dedicated: true`). `breakout.fencingDriver: none` makes it report only;
  `breakout.enabled: false` removes it if you do not run AGs on `ReadWriteOnce` volumes.
- Operator pods run non-root, read-only root filesystem, no capabilities, `RuntimeDefault` seccomp.
  SQL Server pods run as UID 10001 with a read-only root filesystem; privileged containers, root and
  extra capabilities are refused at admission.

## Secrets at rest

All passwords and keys are Kubernetes Secrets: enable encryption at rest for etcd, and copy the key
material outside the cluster ([Backups and encryption](backups-and-encryption.md#key-material)).

## Verify the image

Release images are signed with cosign (keyless) and carry a build provenance attestation:

```bash
cosign verify ghcr.io/evariops/mssql-operator@<digest> \
  --certificate-identity-regexp 'https://github.com/Evariops/mssql-operator/.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```
