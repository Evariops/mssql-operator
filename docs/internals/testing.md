# Testing

| Suite | Runs | Covers |
|---|---|---|
| `tests/MsSqlOperator.Tests` | every build, seconds | controllers and services against fakes, validators, CRD drift, architecture rules (actuation gateway, SQL quoting), the decision core simulated on 200 seeds |
| `tests/MsSqlOperator.IntegrationTests` | CI, needs Docker | the SQL Server side against real `mssql/server` containers (Testcontainers): AG management, logins, procedures, Agent jobs, TDE, service master key, ownership registry, epoch stamps |
| `tests/e2e` | CI (kind) | smoke test: install the chart, create an instance, check admission rejects an invalid one |
| `tests/MsSqlOperator.ClusterQa` | by hand, on a real cluster | HA scenarios with a continuous writer through the listener: failovers, no committed row lost, bounded write interruption ([README](../../tests/MsSqlOperator.ClusterQa/README.md)) |
| chaos campaigns | by hand, on a real cluster | network partitions (CiliumNetworkPolicy), frozen and killed processes, lost volumes, Plan/Observe modes; findings fixed and replayed until every scenario passes |

## Run them

```bash
dotnet run --project tests/MsSqlOperator.Tests
dotnet run --project tests/MsSqlOperator.IntegrationTests      # Docker required
QA_CLUSTER=1 QA_KUBE_CONTEXT=<context> QA_NAMESPACE=mssql-ha-qa \
  dotnet test tests/MsSqlOperator.ClusterQa/MsSqlOperator.ClusterQa.csproj
```

The tests use xUnit v3. `dotnet format --verify-no-changes` runs in CI too.

## Changing a CRD

The CRD YAML files under `charts/mssql-operator/templates/crds/` carry validations and descriptions
written by hand that the generator does not produce. To add a field:

1. add the property to the entity under `src/MsSqlOperator/Entities/`;
2. add it by hand to the CRD YAML;
3. run the unit tests: `CrdGeneratedSchemaMatchesCommittedTests` and `CrdYamlDriftTests` check that the
   two agree.

Do not regenerate the files wholesale: it drops the hand-written validations. If you use the
generator, give it an absolute path
(`dotnet run --project src/MsSqlOperator -- generate /abs/path/charts/mssql-operator/templates/crds`),
otherwise it writes under `src/MsSqlOperator/charts`.

## Test images on a cluster

A pull request labelled `build-image` publishes `ghcr.io/evariops/mssql-operator:pr-<number>` once CI is
green. Install the chart from the branch with `image.tag=pr-<number>` and `image.pullPolicy=Always`.
