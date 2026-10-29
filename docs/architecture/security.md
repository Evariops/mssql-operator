# Security model

This chapter describes the trust boundaries, the permissions of each identity and the reasons behind them, admission, the protection of each network channel, and the residual risks. The corresponding settings are in [Operations](../guide/operating.md#production-hardening).

## Trust boundaries

The namespace is the tenancy boundary. A `SqlUser`, `SqlConfiguration`, `SqlAgentJob`, `SqlStoredProcedure` or `SqlTdeCertificate` can only target instances and AGs in its own namespace: target resolution never crosses namespaces, and the former `target.namespaces` field was removed for that reason. The consequence is that anyone allowed to create these resources in a namespace has DBA-level control over the SQL Servers of that namespace, so RBAC on these kinds is effectively RBAC on the databases.

A SQL Server instance, on the other hand, is a single trust zone, whatever the namespaces of its users. A `SqlConfiguration` changes the server for everyone, and a broad database filter in a `SqlUser` exposes the names of other teams' databases through the view API. The recommended layout is therefore one instance or AG per team, in the team's namespace.

The operator is the most privileged component of the system. It reads the Secrets it needs across the cluster and connects to every SQL Server as `sa`, so its namespace and ServiceAccount should be protected accordingly.

The node is the last boundary. SQL Server runs as an unprivileged user with a read-only root filesystem, and key backups never touch the node's disk ([Key material handling](safety.md#key-material-handling)). A root user on the node can still read the SQL Server process memory and its volumes, and nothing in the operator protects against that.

## Identities

The operator's ServiceAccount ([`rbac.yaml`](../../charts/mssql-operator/templates/rbac.yaml)) manages the objects the operator builds: StatefulSets, Services, ConfigMaps, PodDisruptionBudgets and CiliumNetworkPolicies. It can patch and delete the SQL Server pods, which it needs for routing labels, rolling updates and fencing. It can patch PVCs, which is how volumes grow, but cannot delete them.

Secrets are handled with care. The operator can read, create and update Secrets across the cluster, because the passwords and keys of the resources it manages live in their namespaces, but it cannot delete Secrets, so it cannot remove key material even by mistake. Among the resources of its own API, it creates and deletes only `SqlAvailabilityGroupDatabase` resources, on behalf of `SqlDatabase` resources, and it manages `SqlControlRecord` resources and its Leases.

The last group of permissions serves fencing. The operator reads nodes, PersistentVolumes and CSI drivers, and can delete VolumeAttachments, which is how it isolates an unreachable primary on a NotReady node before a failover. This permission is not granted with `breakout.fencingDriver: none`.

The breakout ServiceAccount ([`breakout.yaml`](../../charts/mssql-operator/templates/breakout.yaml)) can watch and delete SQL Server pods, read nodes, node Leases, PVCs and PVs, and delete VolumeAttachments, which is what relocating a pod from a lost node requires. It can also patch nodes, but only to remove the `NoExecute` taints written by earlier versions; it never writes taints. It runs in its own Deployment (`breakout.dedicated: true`) so that this identity has no other role.

On the SQL Server side, the operator connects as `sa`. A dedicated least-privileged login is not implemented ([Limitations](../limitations.md#security)). Stored procedures are the exception: they are deployed through an operator-managed login that is only `db_ddladmin` on the targeted databases (`deployAs: LeastPrivilege`, the default), so the text of a procedure cannot obtain more than DDL rights in its own database. Deploying as `sa` requires the annotation `mssql-operator.evariops.com/allow-sa-deployments: "true"` on the target.

Server roles follow the same principle of consent from the target. A `SqlUser` can request `sysadmin`, `securityadmin` or `serveradmin`, roles that amount to owning the server, but they are granted only on targets annotated `mssql-operator.evariops.com/allow-privileged-roles: "true"`. The author of a `SqlUser` therefore cannot grant themselves control of a server without the agreement of whoever manages it.

## Admission

The validating webhook rejects the manifests that the operator would refuse to run:

- privileged or root pods, and additional capabilities;
- images from registries outside `operator.allowedImageRegistries`, in addition to `mcr.microsoft.com`;
- floating tags and version downgrades;
- `sp_configure` options that allow code execution (`xp_cmdshell`, `clr enabled`…) without `allowDangerous`;
- Agent jobs owned by a sysadmin, or with `CmdExec` or `PowerShell` steps, without `allowDangerous`;
- SQL identifiers outside the allowed character set.

Admission alone is not a sufficient control, because a webhook can be bypassed: it does not see resources created before it was installed, nor updates to the `/status` subresource, and an administrator can disable it. The rules that protect against code execution are therefore applied a second time when the operator executes them: the three-tier `sp_configure` allow-list (safe, dangerous, unknown), the opt-in for privileged roles and the opt-in for deployments as `sa`. When a validation fails with an internal error, the client receives a generic message and the details go to the operator logs, so that internal state does not leak to the author of a resource.

## Network channels

The webhook and the view API are served on port 9443, over TLS only. The certificate is either generated by the chart, with a CA valid for ten years and a leaf certificate renewed 30 days before expiry and reloaded without restart, or issued by cert-manager. The plain-HTTP port 8080 serves health probes and metrics, and returns 404 for `/validate`, `/apis` and `/openapi`, since the trust model of these paths only holds over TLS.

The view API relies on the aggregation layer for authentication. The kube-aggregator presents its front-proxy client certificate, which the operator verifies against the CA published in the `extension-apiserver-authentication` ConfigMap, and the operator then runs a `SubjectAccessReview` to apply RBAC to the user forwarded by the aggregator. When a follower forwards an `operatorplans` request to the leader, it presents its serving certificate, and each side pins the other's certificate.

The connection from the operator to SQL Server is encrypted by default (`sqlConnection.encrypt: Mandatory`), but the server certificate is not verified (`trustServerCertificate: true`), because a newly created SQL Server only has a self-signed certificate. Until the servers have verifiable certificates, a pod on the network path could impersonate a SQL Server and obtain the `sa` password. Setting `trustServerCertificate: false` with a CA (`sqlConnection.caSecretName`) enables verification, and `encrypt: Strict` uses TDS 8 on SQL Server 2022 and later.

Clients receive the certificate configured in `spec.tls.secretName`, and `forceEncryption: true` rejects unencrypted connections. SQL Server reads the certificate at startup, so a renewed certificate takes effect at the next restart. Between replicas, the database mirroring endpoints authenticate with the per-replica certificates and encrypt with AES.

## Network policies

With `networkPolicy.enabled` (the default), the operator pods can only reach DNS, the API server, SQL Server on ports 1433 and 5022, the other operator pods and, with `allowExternalHttps`, HTTPS destinations for GitHub and S3. Ingress is limited to the webhook and metrics ports, and can be narrowed further with `metricsFrom` and `webhookFrom`. Metrics are served over plain HTTP and must not be exposed outside the cluster.

The SQL Server pods get a CiliumNetworkPolicy when `spec.networkPolicy` is enabled on the instance or the AG. Port 1433 is then reachable only from the same namespace, from `allowedNamespaces` and from the operator pods, and egress is limited to DNS, the other replicas and the destinations listed in `networkPolicy.egress`, such as an SMTP relay, backup storage or a linked server. The Kubernetes API is not among the allowed destinations.

## Secrets

All passwords and keys are Kubernetes Secrets, stored in etcd. Encrypting etcd at rest is the responsibility of the cluster, and the operator cannot enforce it. Losing the cluster also means losing the keys, which is why they must be copied outside the cluster ([Copies outside the cluster](../guide/protecting-data.md#copies-outside-the-cluster)).

## Supply chain

Release images are built from a distroless base, signed with cosign (keyless, GitHub OIDC), and carry an SBOM and a build provenance attestation. CI runs zizmor on the workflows and scans the images. SQL Server images are restricted to builds published by Microsoft ([SQL Server image catalog](overview.md#sql-server-image-catalog)) and floating tags are refused, so the SQL Server build only changes through a spec change.

## SQL injection

Login names, database names, schema names and job steps from the specs all end up in T-SQL. Identifiers are validated at admission and quoted when the SQL is built, literals are quoted, and a test fails the build when SQL-building code interpolates a value without the quoting helpers ([SQL injection](safety.md#sql-injection)). Job steps and procedure definitions are executable code by design. They run with the rights of the job owner or of the deployment login, and the opt-ins described above control how high those rights can go.

## Residual risks

Some risks are known and accepted at this stage, and are also listed in [Limitations](../limitations.md). The operator connects as `sa`, so compromising the operator compromises every SQL Server it manages, and it reads Secrets in every namespace. With the default `trustServerCertificate: true`, it does not authenticate the SQL Servers it connects to. Finally, the webhook and the view API are served by the operator: with the default `webhook.failurePolicy: Fail`, no resource of the operator's kinds can be created or changed while every operator pod is down, and an unavailable aggregated API delays namespace deletions across the cluster.
