# Contributing

The operator is a .NET 10 application with its own reconciliation runtime ([Architecture overview](architecture/overview.md)). This page covers the build, the test suites and the conventions enforced by the build.

## Build and test

```bash
dotnet build MsSqlOperator.sln
dotnet run --project tests/MsSqlOperator.Tests               # unit tests (xUnit v3)
dotnet run --project tests/MsSqlOperator.IntegrationTests    # needs Docker: real SQL Server containers
dotnet format --verify-no-changes                            # also run by CI
```

## Test suites

The unit tests in `tests/MsSqlOperator.Tests` run on every build. They cover the controllers and services against fakes that model the engine's behaviour (a backup starts the log chain, an `ADD DATABASE` without a chain fails), the validators, the CRD drift, and the decision core, simulated in virtual time on 200 seeds with injected partitions, crashes and pauses ([Decision core and simulation](architecture/safety.md#decision-core-and-simulation)). They also include governance tests that check that:

- every destructive call goes through the actuation gateway;
- every resource's `automation` setting is read by the gateway;
- SQL text stays in the manager classes, and no value is interpolated into SQL without the quoting helpers;
- no forbidden statement can be sent;
- cancellations are not swallowed;
- every kind patched by the code is granted in the RBAC;
- every spec field is used.

The integration tests in `tests/MsSqlOperator.IntegrationTests` run the SQL Server side against real `mssql/server` containers with Testcontainers: AG management, logins, procedures, Agent jobs, TDE, the service master key, the ownership registry, epoch stamps, databases and seeding queries. They catch errors that fakes cannot, such as a query on a DMV column that does not exist; an error of that kind, found on a cluster, once left every new database in phase `Seeding`, and the seeding queries have run against a real engine in CI since then.

The end-to-end test in `tests/e2e` installs the chart on kind, creates an instance and checks that admission rejects an invalid one.

The cluster QA suite in `tests/MsSqlOperator.ClusterQa` runs HA scenarios on a real cluster, with a continuous writer through the listener, and checks that no committed row is lost and that write interruptions stay bounded ([README](../tests/MsSqlOperator.ClusterQa/README.md)):

```bash
QA_CLUSTER=1 QA_KUBE_CONTEXT=<context> QA_NAMESPACE=mssql-ha-qa \
  dotnet test tests/MsSqlOperator.ClusterQa/MsSqlOperator.ClusterQa.csproj
```

Chaos campaigns are run manually on a real cluster with the same kind of writer: network partitions with CiliumNetworkPolicy, frozen and killed processes, lost volumes, operator restarts, `Plan` and `Observe` modes. Each finding is fixed and its scenario replayed. Their results are summarized in [Failure modes](failure-modes.md).

## CRD changes

The CRD files in [`charts/mssql-operator/templates/crds/`](../charts/mssql-operator/templates/crds/) contain validations, CEL rules and descriptions written by hand, which the generator does not produce. To add a field:

1. add the property to the entity under `src/MsSqlOperator/Entities/`;
2. add it to the CRD YAML by hand;
3. run the unit tests: `CrdGeneratedSchemaMatchesCommittedTests` and `CrdYamlDriftTests` check that both agree.

Do not regenerate the files wholesale, since that removes the hand-written validations. To compare with the generator's output, pass it an absolute path (`dotnet run --project src/MsSqlOperator -- generate /abs/path/out`); with a relative path it writes under `src/MsSqlOperator/charts`.

## SQL Server image catalog

The tags published by Microsoft are stored in [`SqlServerPublishedTags.g.cs`](../src/MsSqlOperator/Configuration/SqlServerPublishedTags.g.cs) and refreshed with `dotnet run --project src/MsSqlOperator -- refresh-images`. A weekly workflow runs this command and opens a pull request when the list changes.

## Test images

A pull request labelled `build-image` publishes `ghcr.io/evariops/mssql-operator:pr-<number>` once CI passes. Install the chart from the branch with `image.tag=pr-<number>` and `image.pullPolicy=Always`.
