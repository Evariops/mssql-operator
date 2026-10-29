# Safety internals

The mechanisms that keep the operator from doing harm when it is wrong, slow or partitioned. Usage:
[Automation guide](../guides/automation.md).

## Actuation gateway

Every disruptive or destructive action (failover, pod deletion, re-seed, drop of a login, of a
procedure, …) is a typed request to one gateway (`Services/Actuation`). An architecture test enforces
that the methods doing these actions are called from nowhere else.

For each request the gateway:

1. decides, from the resource's automation mode (capped by the operator ceiling), the risk class, the
   approvals and pre-authorizations, and the hourly budget;
2. re-checks the preconditions right before acting;
3. records the decision in the plan (`kubectl get operatorplans`), publishes an event and counts it
   (`mssqloperator_actuation_decisions_total`).

A request's id is `<type>:<target>:<epoch>:<fingerprint>`: stable, so an approval names exactly one
action. A sequence whose first steps cannot be undone by the gateway (a failover demotes the old
primary before promoting) asks the gateway up front whether its last step would run, and does nothing
if not.

## Control record and epoch

Each AG and instance has a [`SqlControlRecord`](../reference/sqlcontrolrecord.md) that survives the
deletion of the resource. It holds:

- the **epoch**, raised by each new leader before it reconciles anything (compare-and-swap on a Lease);
- the operation in progress (**intent**), written before its first effect;
- the durability profile, the removed replicas, the open divergences after a data-loss failover, and
  the fingerprint of the machine key the data was created with.

## Exclusion on the SQL side

Kubernetes leadership is not enough: a leader that lost its Lease may still have a connection open.
At level 1 every write of the operator on an AG carries the epoch, stamped in
`msdb.mssqlop` under an application lock; a write from an older epoch is refused by SQL Server itself.

## Safety levels

New mechanisms are rolled out per AG, by level (`mssql-operator.evariops.com/safety-level`, capped
by `operator.safetyLevelMax`). The level above the current one runs in shadow: it computes and logs
its decisions next to the active path, which lets them be compared before switching.

| Level | Content |
|---|---|
| 0 | original logic and its safety fixes |
| 1 | control record, epoch, SQL-side stamps and lock |
| 2 | quorum watchdog, elections by quorum, listener routing by pod name ([experimental](high-availability.md#safety-level-2-quorum-watchdog-and-elections-experimental)) |

## Isolation between servers

- **Per-server limits**: a server that is slow cannot hold all the workers; connections to one server
  are bounded.
- **Circuit breakers** (`operator.targetCircuitBreakers`): in `Enforce`, a server seen down a moment
  ago fails fast instead of waiting for a timeout.
- **Long operations** (initialization backups) run in a bounded background pool, not in a reconcile.
  The pass that starts one waits a few seconds for it, so a small database joins its AG at once; a
  result is used only for the copy it was taken on (one taken before a failover is taken again).

## Decision core and simulation

The election protocol, the timing budget and the supervisor logic live in `MsSqlOperator.Core`,
without I/O. On every build they are simulated on 200 random seeds (partitions, crashes, delays),
checking that no acknowledged commit is lost and that no stale primary serves reads.
