# Automation, approvals and safety levels

The operator acts on its own: it fails over, re-seeds replicas, drops what a resource no longer
declares. This guide explains how to see what it does, slow it down, or make it ask first.

## Risky actions go through one gate

Every action that is not harmless is classified and decided in one place:

| Risk | Meaning | Examples |
|---|---|---|
| Disruptive | interrupts, loses nothing | lossless failover, re-seed of a secondary, pod fencing, disabling a login |
| Destructive | loses something that exists nowhere else, or cannot be undone | failover with data loss, rejoining a diverged replica, removing a database from an AG, dropping a database, a login, a user, a procedure or a job |

Two actions are never automatic, whatever the settings: regenerating the service master key and
generating a new machine key over existing data.

## Automation mode

Each resource can set how much the operator may do on its own:

```yaml
spec:
  automation:
    mode: Apply        # Observe | Plan | Apply (default)
```

| Mode | The operator… |
|---|---|
| `Apply` | acts (default) |
| `Plan` | decides and records what it would do, does nothing risky |
| `Observe` | same as `Plan`; use it to watch a new resource before trusting the operator with it |

In `Plan` and `Observe`, requests made by annotation (for example `failover-to`) stay in place and run
when you switch back to `Apply`.

An operator-wide ceiling caps every resource's mode: the setting `Operator:Automation:MaxMode`
(environment variable `Operator__Automation__MaxMode` on the operator Deployment; the chart does not
expose it yet).

## See the plan

```console
$ kubectl get operatorplans -n sql
NAME                       RESOURCE                      ACTION     RISK         DECISION   EXECUTED   ID
orders.failover.241a8913   SqlAvailabilityGroup/orders   Failover   Disruptive   Withheld   false      Failover:orders-1:15:241a8913
```

The latest decisions of each resource: what was done (`EXECUTED true`), what would be done, what
waits for an approval. Nothing is stored in etcd; the list lives in the operator's memory. Every
decision is also an event (`ActionPlanned`, `ActionRequiresApproval`, `ActionDenied`).

## Destructive actions need an approval

In `Apply` mode, a destructive action runs on its own only when:

- it cleans up a resource you are deleting;
- your spec asks for it (a database removed from a `SqlUser`, `allowAutomaticDataLossFailover`, …);
- a human asked for it with a dedicated annotation (`force-failover-to` + `allow-data-loss`,
  `approve-data-loss-resume`);
- its type is listed in `spec.automation.preAuthorized`, within one action per resource per hour and
  3 per hour for the whole operator.

Otherwise it waits, with an event `ActionRequiresApproval` that gives its id. Approve it:

```bash
kubectl annotate sqlavailabilitygroup orders -n sql \
  mssql-operator.evariops.com/approve='RemoveDatabaseFromAg:Orders:3:1a2b3c4d'
```

The approval is consumed once the action has run. Pre-authorize a type instead:

```yaml
spec:
  automation:
    preAuthorized: [DropProcedure]
```

Destructive types: `ForceFailoverDataLoss`, `ResumeDivergentReplica`, `RemoveDatabaseFromAg`,
`DropLogin`, `DropDatabaseUser`, `DropProcedure`, `DeleteJob`, `DropDatabase`.

## Safety levels

Availability groups have a second dial: how much of the newer failover machinery is active.

| Level | Adds |
|---|---|
| 0 | the original failover logic, with its safety fixes |
| 1 | the control record ([`SqlControlRecord`](../reference/sqlcontrolrecord.md)), the operator epoch, and the epoch stamps and locks on the SQL side that stop an operator that lost leadership from acting |
| 2 | the quorum watchdog (a SQL Agent job that takes a primary offline when it loses its synchronized secondaries), elections by quorum, listener routing by pod name |

An AG runs at the level of its annotation `mssql-operator.evariops.com/safety-level` (0 without),
capped by the Helm value `operator.safetyLevelMax` (default `1`). The level above runs in **shadow**:
it computes and logs its decisions without applying them, as long as it is within the ceiling.

Recommended today: `safety-level: "1"` on your AGs, ceiling 1. Level 2 is experimental: do not raise
the ceiling to 2 in production. How the levels work: [High availability internals](../internals/high-availability.md).

## Circuit breakers

`operator.targetCircuitBreakers` (`Shadow` by default) protects the operator from a server that does
not answer: in `Enforce`, a connection to a server seen down moments ago fails at once instead of
waiting for its timeout, so one dead server does not slow down the others. `Shadow` only logs what it
would do.
