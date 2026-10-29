# mssql-operator

A Kubernetes operator for Microsoft SQL Server. You declare instances, Always On availability groups,
databases, logins, configuration, jobs and encryption as Kubernetes resources; the operator creates
them and keeps them that way, including failovers, upgrades and repairs.

- **Availability groups without a cluster manager.** The operator watches the replicas, fails over
  automatically, fences the old primary before promoting a new one, and moves the listeners.
- **No silent data loss.** With `durability: Quorum`, every acknowledged commit is on a secondary
  before the client gets its answer; a failover that would lose data waits for a human.
- **Day-two operations.** AG-aware rolling upgrades of SQL Server builds, automatic re-seeding,
  volumes grown online, replicas that hold the log back taken out and added back.
- **Everything around the databases.** Logins and permissions, `sp_configure` and trace flags, SQL
  Agent jobs, stored procedures from Git, transparent data encryption with certificate rotation.
- **Safe by default.** An admission webhook rejects invalid or dangerous manifests; destructive
  actions need an approval; an `Observe` / `Plan` mode shows what the operator would do.

> **Status:** API `mssql-operator.evariops.com/v1alpha1`, still evolving. Releases:
> [GitHub releases](https://github.com/Evariops/mssql-operator/releases).

## Resources

| Resource | Manages |
|---|---|
| [`SqlInstance`](docs/reference/sqlinstance.md) | a standalone SQL Server |
| [`SqlAvailabilityGroup`](docs/reference/sqlavailabilitygroup.md) | an Always On availability group and its listeners |
| [`SqlDatabase`](docs/reference/sqldatabase.md) | a user database on an instance or an availability group: created, adopted, its options kept |
| [`SqlAvailabilityGroupDatabase`](docs/reference/sqlavailabilitygroupdatabase.md) | a database joined to an availability group (managed for you by `SqlDatabase`) |
| [`SqlUser`](docs/reference/sqluser.md) | a login, its database users and permissions |
| [`SqlConfiguration`](docs/reference/sqlconfiguration.md) | `sp_configure`, trace flags, tempdb, Database Mail |
| [`SqlAgentJob`](docs/reference/sqlagentjob.md) | a SQL Server Agent job |
| [`SqlStoredProcedure`](docs/reference/sqlstoredprocedure.md) | stored procedures, inline or from GitHub |
| [`SqlTdeCertificate`](docs/reference/sqltdecertificate.md) | transparent data encryption and certificate rotation |

## Install

```bash
helm install mssql-operator oci://ghcr.io/evariops/charts/mssql-operator \
  --version <release version> \
  --namespace mssql-operator-system --create-namespace
```

Then follow [Getting started](docs/getting-started.md): a standalone instance, then a three-replica
availability group with a database and an application login.

## Documentation

- [Getting started](docs/getting-started.md)
- Guides: [availability groups](docs/guides/availability-groups.md),
  [automation and approvals](docs/guides/automation.md),
  [backups and encryption](docs/guides/backups-and-encryption.md), [users](docs/guides/users.md),
  [configuration, jobs, procedures](docs/guides/configuration.md),
  [observability](docs/guides/observability.md), [security](docs/guides/security.md),
  [upgrading](docs/guides/upgrading.md)
- Reference: [resources](docs/reference/), [Helm values](docs/reference/helm-values.md),
  [view API](docs/reference/view-api.md)
- [How it works inside](docs/internals/)

## Develop

The operator is a .NET 10 application with its own reconciliation runtime.

```bash
dotnet build MsSqlOperator.sln
dotnet run --project tests/MsSqlOperator.Tests               # unit tests (xUnit v3)
dotnet run --project tests/MsSqlOperator.IntegrationTests    # needs Docker: real SQL Server containers
```

The CRDs in [`charts/mssql-operator/templates/crds/`](charts/mssql-operator/templates/crds/) carry
hand-written validations. When you add a field to an entity, add it to the YAML by hand and run the
drift tests (`CrdGeneratedSchemaMatchesCommittedTests`, `CrdYamlDriftTests`); do not regenerate the
files wholesale. More in [internals/testing](docs/internals/testing.md).

## License

mssql-operator is licensed under the [Apache License 2.0](LICENSE).
