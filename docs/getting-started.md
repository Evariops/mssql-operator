# Getting started

This walkthrough installs the operator, creates a standalone instance, then a three-replica availability group with a database and an application login, and ends with a planned failover. It takes about fifteen minutes.

## Prerequisites

- Kubernetes 1.29 or later, `kubectl` and Helm 3.
- A default StorageClass backed by a CSI driver that attaches volumes (`attachRequired: true`), such as a cloud disk or network block storage. With NFS or local-path storage, the operator cannot fence a primary lost with its node, and that failover requires manual action. `allowVolumeExpansion: true` lets the operator grow volumes before they fill up.
- About 4 GiB of memory per SQL Server pod, and three nodes for the availability group so that each replica runs on its own node.
- Optionally Cilium, for the network policies of the SQL Server pods.

## 1. Install the operator

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system --create-namespace

kubectl -n mssql-operator-system rollout status deploy/mssql-operator
kubectl get crds | grep mssql-operator.evariops.com
kubectl get apiservice v1alpha1.view.mssql-operator.evariops.com
```

The chart installs the CRDs, two operator pods (one holds the leader Lease and reconciles, both serve the admission webhook and the view API), and the breakout controller, which handles SQL Server pods stuck on lost nodes. Available versions are listed on the [releases page](https://github.com/Evariops/mssql-operator/releases).

## 2. Create a standalone instance

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
  acceptEula: true
  edition: Developer
  saPasswordSecretRef: { name: reporting-sa, key: password }
  memory: { total: 4Gi }
  storage:
    data: { size: 10Gi }
    log:  { size: 5Gi }
EOF

kubectl -n sql get sqlinstance reporting -w      # until PHASE is Running
```

The operator resolves `2022` to the default cumulative update of that version, creates a StatefulSet with one pod and its volumes, and generates a machine key that it stores in a Secret, so that the service master key stays decryptable across pod restarts. Once SQL Server is up, it sets the memory limits from `memory.total` and stores a backup of the service master key in a Secret. `kubectl -n sql describe sqlinstance reporting` shows the conditions and events, and `kubectl -n sql get secrets` lists the key material.

To connect with the `sqlcmd` shipped in the image:

```bash
SA=$(kubectl -n sql get secret reporting-sa -o jsonpath='{.data.password}' | base64 -d)
kubectl -n sql exec reporting-0 -c mssql -- /opt/mssql-tools18/bin/sqlcmd \
  -S reporting.sql.svc.cluster.local -U sa -P "$SA" -C -Q "SELECT @@VERSION"
```

## 3. Create an availability group

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
  acceptEula: true
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

kubectl -n sql get sqlavailabilitygroup orders -w   # until PHASE is Running and PRIMARY is set
```

The operator starts the three replicas, creates an endpoint certificate on each one and imports the public keys on the others, creates the AG on `orders-0` and joins `orders-1` and `orders-2`. It creates the Services `orders-listener` (read/write, follows the primary) and `orders-ro` (read-only, synchronized readable secondaries).

`durability: Quorum` keeps `REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT` at 1 or more: every acknowledged commit is on a synchronized secondary, so an automatic failover never loses one, and writes block when no secondary is synchronized. `safety-level: "1"` enables the control record, the operator epoch and the epoch checks on the SQL Server side. Both are described in [Availability groups](guide/availability-groups.md).

## 4. Add a database

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

The operator creates `Orders` on the primary and records its ownership in an extended property of the database. It then takes the initialization backup, adds the database to the AG and lets automatic seeding copy it to the secondaries. With `logBackup.mode: Local`, the operator backs up the transaction log when it is half full and no other backup truncated it. The default `deletionPolicy: Retain` keeps the database when the `SqlDatabase` is deleted.

## 5. Create an application login

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

The login is created on the three replicas with the same SID, so the database user keeps working after a failover. The database user is created on the primary and replicated by the AG. Applications connect to `orders-listener.sql.svc.cluster.local,1433` (database `Orders`, user `orders_app`), and to `orders-ro.sql.svc.cluster.local` for read-only queries.

## 6. Run a planned failover

```bash
kubectl -n sql annotate sqlavailabilitygroup orders mssql-operator.evariops.com/failover-to=orders-1
kubectl -n sql get sqlavailabilitygroup orders -w   # PRIMARY becomes orders-1
kubectl -n sql get agreplicastates                  # live state of each replica
```

The operator checks that `orders-1` is synchronized, demotes `orders-0`, promotes `orders-1` and moves the listener, typically in two to four seconds. It removes the annotation once the failover is complete. The sequence is visible with `kubectl -n sql get events --field-selector involvedObject.name=orders`.

Deleting the primary pod (`kubectl -n sql delete pod orders-1`) usually does not cause a failover: the pod restarts and SQL Server comes back as primary before the 20-second detection window ends. [Failure modes](failure-modes.md) describes the cases where it does not.

## Clean up

```bash
kubectl -n sql delete sqluser orders-app
kubectl -n sql delete sqldatabase orders-db
kubectl -n sql delete sqlavailabilitygroup orders
kubectl -n sql delete sqlinstance reporting
```

The PVCs and the key Secrets are kept. [Decommissioning](guide/protecting-data.md#decommissioning) lists the commands to remove them, in the right order.

## Next steps

- [Backups and encryption](guide/protecting-data.md): the operator does not schedule full backups and does not copy the key Secrets outside the cluster. Both have to be set up before production.
- [Operations](guide/operating.md): alerting on `NeedsAttention`, and production hardening.
- [Limitations](limitations.md).
