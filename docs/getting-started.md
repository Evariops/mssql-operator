# Getting started

From an empty cluster to an application connected to a three-replica availability group, in about
fifteen minutes.

## Prerequisites

- Kubernetes 1.29 or later, `kubectl`, Helm 3.
- A default StorageClass backed by a CSI driver that attaches volumes (not NFS or local-path: the
  operator must be able to detach a lost primary's volumes before failing over), ideally with
  `allowVolumeExpansion: true` (volumes are then grown online when they fill up).
- Memory: about 4 GiB per SQL Server pod. For an availability group, three nodes so that each replica
  gets its own.
- Optional: Cilium, for the network policies of the SQL Server pods.

## 1. Install the operator

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system --create-namespace

kubectl -n mssql-operator-system rollout status deploy/mssql-operator
kubectl get crds | grep mssql-operator.evariops.com
kubectl get apiservice v1alpha1.view.mssql-operator.evariops.com
```

This installs the CRDs, two operator replicas (one leads), the admission webhook with a generated
certificate, the [view API](reference/view-api.md), and the breakout controller that handles SQL
Server pods stuck on a lost node. Versions: [GitHub releases](https://github.com/Evariops/mssql-operator/releases).

## 2. A standalone instance

```bash
kubectl create namespace sql
kubectl -n sql create secret generic reporting-sa \
  --from-literal=password="$(openssl rand -base64 18)Aa1!"

kubectl apply -f - <<'EOF'
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlInstance
metadata:
  name: reporting
  namespace: sql
spec:
  version: "2022"
  edition: Developer
  saPasswordSecretRef: { name: reporting-sa, key: password }
  memory: { total: 4Gi }
  storage:
    data: { size: 10Gi }
    log:  { size: 5Gi }
EOF

kubectl -n sql get sqlinstance reporting -w      # until PHASE is Running
```

Connect (the SQL Server image ships `sqlcmd`):

```bash
SA=$(kubectl -n sql get secret reporting-sa -o jsonpath='{.data.password}' | base64 -d)
kubectl -n sql exec reporting-0 -c mssql -- /opt/mssql-tools18/bin/sqlcmd \
  -S reporting.sql.svc.cluster.local -U sa -P "$SA" -C -Q "SELECT @@VERSION"
```

## 3. An availability group

```bash
kubectl -n sql create secret generic orders-sa  --from-literal=password="$(openssl rand -base64 18)Aa1!"
kubectl -n sql create secret generic orders-dmk --from-literal=password="$(openssl rand -base64 18)Aa1!"

kubectl apply -f - <<'EOF'
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata:
  name: orders
  namespace: sql
  annotations:
    mssql-operator.evariops.com/safety-level: "1"
spec:
  edition: Developer
  replicas:
    count: 3
    synchronousCount: 3
    durability: Quorum
  saPasswordSecretRef: { name: orders-sa, key: password }
  masterKeyPasswordSecretRef: { name: orders-dmk, key: password }
  hadrCertificate: { source: Generate, model: PerReplica }
  instanceTemplate:
    version: "2022"
    memory: { total: 4Gi }
    storage:
      data: { size: 10Gi }
      log:  { size: 5Gi }
  listener:
    service: { type: ClusterIP }
  readOnlyListener:
    service: { type: ClusterIP }
EOF

kubectl -n sql get sqlavailabilitygroup orders -w   # until PHASE is Running, PRIMARY set
```

The operator starts the three replicas, exchanges their certificates, creates the AG and joins the
secondaries. It creates `orders-listener` (read/write) and `orders-ro` (read-only).

## 4. A database in the AG

```bash
kubectl apply -f - <<'EOF'
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlDatabase
metadata:
  name: orders-db
  namespace: sql
spec:
  availabilityGroupRef: { name: orders }
  databaseName: Orders
  availabilityGroup:
    logBackup: { mode: Local, retentionHours: 72 }
EOF

kubectl -n sql get sqldb orders-db -w               # until Ready
```

The operator creates the database on the primary, takes its initialization backup, adds it to the AG
and seeds the secondaries. Deleting the `SqlDatabase` keeps the database (`deletionPolicy: Retain`).

## 5. An application login

```bash
kubectl -n sql create secret generic orders-app --from-literal=password="$(openssl rand -base64 18)Aa1!"

kubectl apply -f - <<'EOF'
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlUser
metadata:
  name: orders-app
  namespace: sql
spec:
  target: { availabilityGroups: [orders] }
  login:
    name: orders_app
    passwordSecretRef: { name: orders-app, key: password }
  databasePermissions:
    - databases: [Orders]
      roles: [db_datareader, db_datawriter]
EOF
```

The application connects to `orders-listener.sql.svc.cluster.local,1433` (database `Orders`, user
`orders_app`), and to `orders-ro.sql.svc.cluster.local` for read-only queries.

## 6. Try a failover

```bash
kubectl -n sql annotate sqlavailabilitygroup orders mssql-operator.evariops.com/failover-to=orders-1
kubectl -n sql get sqlavailabilitygroup orders -w   # PRIMARY becomes orders-1
kubectl -n sql get agreplicastates                  # live state of each replica
```

## Next

- [Availability groups](guides/availability-groups.md): durability, failovers, upgrades, self-healing.
- [Backups and encryption](guides/backups-and-encryption.md): **back up your databases and the key
  Secrets**, the operator does not do it for you.
- [Observability](guides/observability.md): alert on `NeedsAttention`.
- [Security](guides/security.md) before going to production.
