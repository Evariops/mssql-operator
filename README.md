# mssql-operator

MsSqlOperator runs Microsoft SQL Server on Kubernetes declaratively, the way you actually want to
run a database in gitops. Instead of hand-crafting StatefulSets, wiring up Always On Availability
Groups by hand, and writing runbooks for the day a node falls over, you describe the database you
want as a Kubernetes resource and let the operator reconcile reality toward it. It provisions
instances, bootstraps availability groups, routes read traffic, rolls out version upgrades, and
orchestrates failover when things go wrong.

It is a .NET operator built on a home-grown reconciliation runtime (list, watch, relist, a
single-flight work queue, leader election via a `Lease`). It speaks plain Kubernetes: custom
resources in, real SQL Server topologies out.

> **Status.** The API group is `mssql-operator.evariops.com/v1alpha1`; the CRDs and behaviour are
> still evolving. For the version to install, see the
> [GitHub releases](https://github.com/Evariops/mssql-operator/releases) (the published chart's
> `appVersion` pins the matching operator image).

## What it manages

Everything the operator does is driven by a family of custom resources in the
`mssql-operator.evariops.com/v1alpha1` API group. Each one has its own reference page under
[`docs/crds/`](docs/crds/) with the full spec, status, validation rules, and a worked example.

| Resource | What it manages |
|---|---|
| [`SqlInstance`](docs/crds/sqlinstance.md) | A single SQL Server: StatefulSet, storage, service, and lifecycle. |
| [`SqlAvailabilityGroup`](docs/crds/sqlavailabilitygroup.md) | An Always On Availability Group: replicas, listeners, read routing, operator-orchestrated failover and rolling updates. |
| [`SqlAvailabilityGroupDatabase`](docs/crds/sqlavailabilitygroupdatabase.md) | Databases joined into an availability group, with seeding and resynchronisation. |
| [`SqlConfiguration`](docs/crds/sqlconfiguration.md) | `sp_configure` settings, trace flags, and tempdb layout applied to matching instances. |
| [`SqlUser`](docs/crds/sqluser.md) | Logins, database users, and role membership. |
| [`SqlStoredProcedure`](docs/crds/sqlstoredprocedure.md) | Stored procedures deployed inline or from a GitHub repository, optionally following releases. |
| [`SqlAgentJob`](docs/crds/sqlagentjob.md) | SQL Server Agent jobs: steps, schedules, and notifications. |
| [`SqlTdeCertificate`](docs/crds/sqltdecertificate.md) | Transparent Data Encryption certificates and the database master key. |

The two cornerstones are `SqlInstance` and `SqlAvailabilityGroup`. The rest compose on top of them by
selecting their targets with labels. Availability groups in particular run with `CLUSTER_TYPE=NONE`
and `FAILOVER_MODE=MANUAL`, so there is no Windows/Pacemaker cluster underneath. The operator itself
is the cluster manager, which lets it treat failover, split-brain fencing, and rolling updates
as first-class concerns. An admission webhook checks every resource before it is persisted, so
mistakes are rejected at `kubectl apply` time rather than discovered later in a reconcile loop.

## Getting started

### Prerequisites

You will need a Kubernetes cluster (anything from a local `kind`/`minikube` up to a managed cloud
cluster), `kubectl` pointed at it, and Helm 3. You also need a default `StorageClass`, or you can
substitute your own in the manifests below, which reference `standard` for a single node and
`fast-ssd` for an availability group. SQL Server is memory-hungry, so give the cluster some room.
A single Developer-edition instance is happy with ~4Gi, while a three-replica Enterprise availability
group wants real nodes behind it.

### Install the operator

The operator ships as a versioned Helm chart published to GHCR on every release. Install a
published version (recommended — the chart's `appVersion` pins the matching operator image):

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system \
  --create-namespace
```

For development, you can install the chart straight from a checkout (set `image.tag` explicitly —
the in-repo `Chart.yaml` appVersion does not track main):

```bash
git clone https://github.com/Evariops/mssql-operator.git
cd MsSqlOperator

helm install mssql-operator ./charts/mssql-operator \
  --namespace mssql-operator-system \
  --create-namespace \
  --set image.tag=latest
```

### Verifying the image

Release images are signed with cosign (keyless, GitHub OIDC) and carry a build-provenance
attestation. The pushed digest is printed in the release notes. Verify before deploying:

```bash
cosign verify ghcr.io/evariops/mssql-operator@<digest> \
  --certificate-identity-regexp 'https://github.com/Evariops/mssql-operator/.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

That single command does more than drop a Deployment into the cluster. Helm applies the CRDs, creates
the operator's `ServiceAccount` and `ClusterRole`. Because the admission webhook is on by
default, it also generates a self-signed CA and serving certificate at install time and wires up the
`ValidatingWebhookConfiguration` for you. No cert-manager dependency, and no private keys committed
anywhere. The split-brain fencing controller (the "breakout" component, described below) is also
enabled by default and runs inside the same operator pod.

Watch it come up:

```bash
kubectl -n mssql-operator-system rollout status deploy/mssql-operator
```

### Run your first instance

With the operator running, you describe a database and apply it. You are not writing a StatefulSet,
you are stating intent. The manifest below creates a namespace, the SA-password secret, and a single
instance in one go:

```bash
kubectl apply -f - <<'EOF'
apiVersion: v1
kind: Namespace
metadata:
  name: mssql-demo
---
apiVersion: v1
kind: Secret
metadata:
  name: mssql-sa-password
  namespace: mssql-demo
type: Opaque
stringData:
  password: "YourStr0ngP@ssw0rd!"
---
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlInstance
metadata:
  name: my-sql-instance
  namespace: mssql-demo
spec:
  mode: SingleNode
  version: "2022"
  edition: Developer
  saPasswordSecretRef:
    name: mssql-sa-password
    key: password
  storage:
    data: { size: 10Gi, storageClassName: standard }
    log:  { size: 5Gi,  storageClassName: standard }
  service:
    type: ClusterIP
    port: 1433
EOF
```

The operator provisions the StatefulSet, the persistent volumes, and a `ClusterIP` service on port
1433. Once the pod is ready you can connect from inside the cluster with a throwaway `sqlcmd` pod,
using the SA password from the secret:

```bash
kubectl -n mssql-demo run sqlcmd --rm -it --restart=Never \
  --image=mcr.microsoft.com/mssql-tools \
  -- /opt/mssql-tools/bin/sqlcmd \
     -S my-sql-instance.mssql-demo.svc,1433 -U sa -P 'YourStr0ngP@ssw0rd!' \
     -Q "SELECT @@VERSION"
```

From here the other resources compose on top by selecting the instance with labels: tune it with a
[`SqlConfiguration`](docs/crds/sqlconfiguration.md), grant access with a [`SqlUser`](docs/crds/sqluser.md),
and deploy schema with a [`SqlStoredProcedure`](docs/crds/sqlstoredprocedure.md). Every reference page
opens with a minimal example you can copy.

### Step up to an Availability Group

When you are ready for high availability, the `SqlAvailabilityGroup` resource is where the operator
earns its keep. This manifest asks for three replicas (two synchronous) with a load-balanced
read/write listener and a separate read-only listener:

```bash
kubectl apply -f - <<'EOF'
apiVersion: v1
kind: Namespace
metadata:
  name: mssql-ag-demo
---
apiVersion: v1
kind: Secret
metadata:
  name: mssql-sa-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "YourStr0ngP@ssw0rd!"
---
apiVersion: v1
kind: Secret
metadata:
  name: mssql-master-key-password
  namespace: mssql-ag-demo
type: Opaque
stringData:
  password: "M@sterK3yP@ssw0rd!"
---
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: my-ag
  namespace: mssql-ag-demo
spec:
  edition: Enterprise
  replicas:
    count: 3
    synchronousCount: 2
  saPasswordSecretRef:
    name: mssql-sa-password
    key: password
  masterKeyPasswordSecretRef:
    name: mssql-master-key-password
    key: password
  hadrCertificate:
    source: Generate
    model: PerReplica
  instanceTemplate:
    version: "2022"
    storage:
      data: { size: 20Gi, storageClassName: fast-ssd }
      log:  { size: 10Gi, storageClassName: fast-ssd }
  listener:
    port: 1433
    service: { type: LoadBalancer }
  readOnlyListener:
    port: 1434
    service: { type: ClusterIP }
  antiAffinity:
    type: Required
    topologyKey: kubernetes.io/hostname
  updateStrategy:
    type: RollingUpdate
    rollingUpdate:
      maxUnavailable: 1
      pauseBetweenPodsSeconds: 120
EOF
```

Behind that spec the operator bootstraps each replica, exchanges the HADR certificates, creates the
availability group, joins the secondaries, and then maintains the whole thing: it labels pods with
their live role (`ag-role`, `readable`, `listener-ready`, `read-intent-ready`), keeps the read/write
service pointed at the primary and the read-only service pointed at healthy readable secondaries, and
continuously adjusts the required-synchronous set to keep the group write-available when a
synchronous secondary drops. That adjustment trades durability for write availability: by default it
can lower `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` to 0 (commits no longer wait for any
secondary), emitting a `RequiredSyncLowered` warning event each time and restoring the value as soon
as a synchronous secondary reconnects. Deployments that must never weaken durability should pin a
floor (see the availability-group reference).

## Living with it: day-two operations

The reason to hand SQL Server to an operator is everything that happens *after* the first
`kubectl apply`.

**Rolling updates** are availability-group-aware. When you change the image or template, the operator
walks the replicas one at a time using an `OnDelete` StatefulSet, never taking down more than the
group can tolerate, draining and failing over the primary before it is replaced, and pausing between
instances so the group can resynchronise. If something stalls, the update goes sticky-`Failed` and
waits for you rather than thrashing.

**Failover** is orchestrated, not hoped for. Because the group runs without an external cluster
manager, the operator decides when and where to fail over, fences the old primary to prevent
split-brain, and exposes manual override through annotations when you need to take the wheel. A planned
failover keeps your synchronous secondaries readable throughout. A forced one is gated behind explicit
intent so you never lose data by accident.

**Breakout** is the safety net for the nastiest Kubernetes failure mode. When a node dies hard, a pod
backed by a `ReadWriteOnce` volume can sit in `Terminating` for six minutes or more while Kubernetes
refuses to reattach the volume elsewhere, leaving your replica down the whole time. The breakout
controller watches for exactly this situation, confirms the node is genuinely `NotReady` and the pod
is genuinely stuck, fences the volume off the dead node by deleting its `VolumeAttachment`, and
force-deletes the pod so the StatefulSet can reschedule it in seconds rather than minutes. It runs
in-process inside the operator (which is why the operator's `ClusterRole` carries node and
volume-attachment permissions), it rate-limits itself to avoid fencing half the cluster at once, and
it un-fences nodes once they come back and stay healthy. Real fencing is the default. If you would
rather watch it reason before it acts, set `breakout.fencingDriver: none` to run it as a dry run.

## Configuration

Everything is driven through Helm values. The defaults are sensible for a real install (webhook on,
breakout on, the view API on, an operator NetworkPolicy on, the published image pinned to the chart's
`appVersion`), but the knobs you reach for most often are the image (`image.repository`, `image.tag`),
the admission webhook (`webhook.enabled`, `webhook.failurePolicy`, which is `Fail` by default so
validation is genuinely enforced), and the breakout controller (`breakout.enabled`,
`breakout.fencingDriver`, and its timing thresholds).

Two security defaults are worth knowing about. First, the operator-to-SQL connection
(`sqlConnection.*`) encrypts traffic but ships with `trustServerCertificate: true`, so the SQL
server's certificate is *not* authenticated out of the box — set `encrypt: Strict` with a trusted
`serverCertificate` to close the in-cluster MITM window. Second, an egress-restricting
`NetworkPolicy` for the operator pod is enabled by default (`networkPolicy.enabled`).
See [`charts/mssql-operator/values.yaml`](charts/mssql-operator/values.yaml) for the full set with
inline documentation.

The operator also serves an **aggregated read-only view API** (`viewApi.enabled`, on by default) that
returns live per-item detail — per-database TDE state, per-procedure deployments, login/role
membership, SQL Agent run history, AG replica health — computed fresh from SQL Server on each request
rather than persisted in CRD status. Query it with plain `kubectl get tdedatabasestates` /
`storedproceduredeployments` / `sqlusertargets` / `sqlagentjobruns` / `agreplicastates`; see
[`docs/crds/view-api.md`](docs/crds/view-api.md).

The CRDs ship as ordinary chart templates under
[`charts/mssql-operator/templates/crds/`](charts/mssql-operator/templates/crds/) (not Helm's special
top-level `crds/` directory), so both `helm install` and `helm upgrade` apply the current CRD schema —
you do not need to reapply them by hand on upgrade. They carry the `helm.sh/resource-policy: keep`
annotation, so a `helm uninstall` leaves the CRDs (and your custom resources) in place rather than
deleting them. If you ever need to apply them out of band, point `kubectl` at that directory:

```bash
kubectl apply -f charts/mssql-operator/templates/crds/
```

## Building from source

The operator targets .NET 10. To build and test it:

```bash
dotnet restore MsSqlOperator.sln
dotnet build   MsSqlOperator.sln -c Release
dotnet test    tests/MsSqlOperator.Tests/MsSqlOperator.Tests.csproj
```

The CRD YAML that ships in the chart is generated from the C# entity types, and a drift test keeps the
two in sync. After changing an entity, regenerate the chart CRDs with:

```bash
dotnet run --project src/MsSqlOperator -- generate charts/mssql-operator/templates/crds
```

## Where to look next

The [`docs/crds/`](docs/crds/) reference pages each open with a copy-pasteable minimal manifest and
then document every field, so they are the fastest way to go from "installed" to a resource that does
what you want.

## License

mssql-operator is licensed under the [Apache License 2.0](LICENSE).
