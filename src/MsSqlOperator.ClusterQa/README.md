# MsSqlOperator.ClusterQa

Dedicated **cluster QA** suite: real HA / resilience scenarios run against a **live Kubernetes
cluster** with a deployed operator and **real SQL Server databases holding real data**. This is
not a unit or per-PR integration test — it deploys an Always On Availability Group, drives a
continuous writer through the listener, triggers failovers, and asserts **no committed-row loss**
and a **bounded write interruption**.

## Why it is separate

These scenarios need a multi-node cluster, real `mssql/server` pods (amd64), minutes of runtime,
and cluster credentials. They must never run on every PR. The project is part of
`MsSqlOperator.sln` so it is **built and format-checked** in CI, but the CI test steps only run
`MsSqlOperator.Tests` and `MsSqlOperator.IntegrationTests`, so these scenarios never execute there.
Every scenario is also guarded by `[ClusterFact]`, which **skips** unless `QA_CLUSTER=1`.

## Running it

```bash
QA_CLUSTER=1 \
QA_KUBE_CONTEXT=<context> \
QA_NAMESPACE=mssql-ha-qa \
dotnet test src/MsSqlOperator.ClusterQa/MsSqlOperator.ClusterQa.csproj
```

Requirements on the runner: `kubectl` on PATH with access to the target context, and an operator
already installed in `QA_OPERATOR_NS`. SQL Server is amd64-only, so the cluster must have at least
one schedulable `amd64` node (and, for *node*-fault tolerance, two — see below).

### Configuration (env vars)

| Var | Default | Meaning |
|---|---|---|
| `QA_CLUSTER` | _(off)_ | Master gate. `1` to run; otherwise every scenario is skipped. |
| `QA_KUBE_CONTEXT` | current | kubeconfig context to target. |
| `QA_OPERATOR_NS` | `mssql-operator` | Namespace the operator runs in. |
| `QA_NAMESPACE` | `mssql-ha-qa` | Namespace the QA workloads live in (wiped on teardown). |
| `QA_SQL_ARCH` | `amd64` | Node arch the SQL pods are pinned to. |
| `QA_MAX_FAILOVER_GAP_S` | `30` | Max tolerated write interruption (s) on a planned failover. |
| `QA_KEEP` | _(off)_ | `1` keeps the namespace after the run for post-mortem. |

## Topology caveat (node-fault tolerance)

A faithful "survives a node loss" test needs **two schedulable amd64 nodes** so the AG replicas
land on different hosts (`requiredSelfAntiAffinity: topologyKey=hostname`). On a single-amd64-node
cluster the replicas co-locate, so the suite still validates the operator's **failover
orchestration and listener repointing** (process-level no-interruption) but cannot exercise a
physical node failure — that node is a single point of failure. On a multi-node cluster, set
`QA_ANTIAFFINITY=1` so the replicas spread one-per-node and a node fault becomes a real test.

## Assets

`assets/` holds the manifests and seed SQL (AG, AG-database membership, the in-cluster writer
probe, real-data seed) consumed by the QA fixture.
