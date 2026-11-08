# mssql-operator

A Kubernetes operator for Microsoft SQL Server on Linux. It manages standalone instances and Always On availability groups, and the objects inside them: databases, logins and permissions, server configuration, SQL Agent jobs, stored procedures and transparent data encryption.

Availability groups run with `CLUSTER_TYPE = NONE`, without Windows Failover Clustering or Pacemaker. The operator acts as the cluster manager: it monitors the replicas, decides failovers, isolates the old primary before promoting a secondary, and moves the listener.

```yaml
apiVersion: mssql-operator.evariops.com/v1alpha1
kind: SqlAvailabilityGroup
metadata: { name: orders, namespace: sql }
spec:
  acceptEula: true
  edition: Enterprise
  replicas: { count: 3, synchronousCount: 3, durability: Quorum }
  saPasswordSecretRef: { name: orders-sa, key: password }
  masterKeyPasswordSecretRef: { name: orders-dmk, key: password }
  hadrCertificate: { source: Generate, model: PerReplica }
  instanceTemplate:
    version: "2022"
    memory: { total: 16Gi }
    storage: { data: { size: 50Gi }, log: { size: 20Gi } }
  listener: { service: { type: ClusterIP } }
```

## Failover guarantees

In a clusterless AG, SQL Server's only promotion command does not stop the old primary, so a failover is safe only if the old primary is known to be unable to write. The operator therefore promotes a secondary only after establishing this: it demotes the old primary when it still answers, and otherwise relies on a pod deletion confirmed by the kubelet or on the detachment of its volumes. When none of these can be confirmed, the failover is aborted rather than risking two writers.

The second guarantee concerns data. With `durability: Quorum`, a commit is acknowledged only once it is hardened on a synchronized secondary, and an automatic failover only promotes a replica that holds every acknowledged commit. A failover that could lose data is never automatic by default: it requires a human decision, and is recorded when it happens.

Every decision is published as a Kubernetes event. When the operator needs a human, it raises the `NeedsAttention` condition with a message that states what it could not establish, and `Plan` mode shows what it would do without doing it.

These behaviours were tested in chaos campaigns on a real cluster, with a client committing every 100 ms through the listener. The campaigns covered about forty scenarios: frozen and isolated primaries, network partitions, lost volumes, and the operator killed during a failover. The bugs they found were fixed and the scenarios replayed. [Failure modes](docs/failure-modes.md) gives the behaviour and the measurements for each case.

## Resources

| Resource | Manages |
|---|---|
| `SqlInstance` | a standalone SQL Server |
| `SqlAvailabilityGroup` | an availability group, its listeners, failovers and rolling updates |
| `SqlDatabase` | a user database, created or adopted, with its options and files |
| `SqlAvailabilityGroupDatabase` | a database joined to an AG: seeding, resume, re-seed, log backups |
| `SqlUser` | a login, its database users and permissions, with optional expiry |
| `SqlConfiguration` | `sp_configure`, trace flags, tempdb, Database Mail |
| `SqlAgentJob` | a SQL Server Agent job |
| `SqlStoredProcedure` | stored procedures, inline or from a GitHub repository |
| `SqlTdeCertificate` | transparent data encryption and certificate rotation |

## Status and limitations

The API is `mssql-operator.evariops.com/v1alpha1` and can still change. Releases are published on [GitHub](https://github.com/Evariops/mssql-operator/releases).

The operator does not schedule full or differential backups, does not span several Kubernetes clusters, and connects to SQL Server as `sa`. A failover after a hard failure of the primary takes about a minute and a half. Safety level 2 is not safe to enforce yet. The complete list is in [Limitations](docs/limitations.md).

## Install

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system --create-namespace
```

[Getting started](docs/getting-started.md) then creates a standalone instance and a three-replica availability group with a database and a login, and ends with a planned failover.

## Documentation

- Usage: [Getting started](docs/getting-started.md), [Availability groups](docs/guide/availability-groups.md), [Databases, logins and server objects](docs/guide/databases-and-access.md), [Backups and encryption](docs/guide/protecting-data.md), [Operations](docs/guide/operating.md)
- Incidents: [Failure modes](docs/failure-modes.md)
- Design: [Architecture overview](docs/architecture/overview.md), [Availability group internals](docs/architecture/availability.md), [Safety mechanisms](docs/architecture/safety.md), [Security model](docs/architecture/security.md)
- Reference: [Resources](docs/reference/resources.md), [Helm values, view API and metrics](docs/reference/operator.md)
- Licensing: [SQL Server licensing](docs/licensing.md)
- Development: [Contributing](docs/contributing.md)

## License

mssql-operator is licensed under the [Apache License 2.0](LICENSE). SQL Server itself needs a license from Microsoft: see [SQL Server licensing](docs/licensing.md). Microsoft and SQL Server are trademarks of Microsoft Corporation; this project is not affiliated with Microsoft.
