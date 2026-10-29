# Architecture

How the operator is built, for contributors and the curious. Usage is in the [guides](../guides/).

## Components

```
┌──────────────────────── operator Deployment (2 pods) ────────────────────────┐
│  leader only            │  every pod                                          │
│  ─ reconcilers (one     │  ─ admission webhook  (HTTPS 9443)                  │
│    runtime per kind)    │  ─ view API           (aggregated APIService)       │
│  ─ AG supervisor        │  ─ /healthz, /readyz, /metrics (HTTP 8080)          │
└──────────────────────────────────────────────────────────────────────────────┘
┌──────── breakout Deployment (2 pods, leader-elected) ────────┐
│  pods stuck on lost nodes, volume detach, node taints        │
└──────────────────────────────────────────────────────────────┘
```

- **Reconcilers** run on the leader only (Lease `mssql-operator-leader`). A pod that loses the Lease
  exits, and Kubernetes restarts it: it never keeps acting after another pod took over.
- **Admission webhook** and **view API** run on every pod, so they keep answering during a leader
  change. A follower asks the leader for the plans (`operatorplans`) it does not hold.
- **Breakout** runs in its own Deployment and ServiceAccount: the only identity allowed to taint nodes
  and delete VolumeAttachments.
- **`MsSqlOperator.Core`**: the decision logic of the failover protocol (elections, timing budget,
  supervisor), pure and without I/O. It is simulated deterministically on every build.

## Runtime

The operator does not use an operator SDK. `src/MsSqlOperator/Operator/` implements the runtime,
following the `controller-runtime` model:

- one watch per resource kind (list, watch, relist on expiry), feeding a de-duplicated work queue;
- workers fetch the fresh object and call the controller: decisions depend on the current state,
  never on the event that woke them up (level-triggered);
- owned objects (StatefulSets, Services, PVCs, SQL Server pods, labelled Secrets) are watched too, and
  a change wakes up their owner: a deleted Service is recreated within seconds;
- finalizers clean up SQL Server objects (logins, jobs, procedures) before a resource is removed.

Availability groups have one more loop: the **AG supervisor** reads the state of every replica every
10 seconds and wakes the AG controller when something changes (a role, a synchronization state).

## Inside a reconcile

A controller converges by small, idempotent steps and records where it is in `status`:

1. resolve the targets and Secrets;
2. converge Kubernetes objects (StatefulSet, Services, PDB, network policy);
3. connect to SQL Server and converge what lives there;
4. write `status.phase` and `status.conditions`, publish events for what changed.

Status stays small: it holds a convergence signal and counts. Anything large or volatile (lag per
replica, per-database states, job history) is computed on demand by the [view API](../reference/view-api.md).

Every action that interrupts or loses something goes through the **actuation gateway**, which decides
under the resource's automation mode ([Safety](safety.md)).

## Where things live

| Path | Content |
|---|---|
| `src/MsSqlOperator/Controllers/` | one controller per resource kind |
| `src/MsSqlOperator/Services/SqlServer/` | everything that talks to SQL Server (AG management, failover orchestration, backups, keys) |
| `src/MsSqlOperator/Services/Actuation/` | the actuation gateway and the plans |
| `src/MsSqlOperator/Services/Control/` | control record, epochs, durability, watchdog, elections shell |
| `src/MsSqlOperator/Services/Breakout/` | the breakout controller |
| `src/MsSqlOperator/Operator/` | the runtime, webhooks, view API, CRD generation |
| `src/MsSqlOperator.Core/` | pure decision logic and its simulation |
| `src/MsSqlOperator/Entities/` | the CRD types |
| `charts/mssql-operator/` | the Helm chart and the CRDs |
