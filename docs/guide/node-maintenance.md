# Node maintenance

Node upgrades and reboots stop the SQL Server pods of the node. The operator watches for the signals that announce such a stop, so that it can prepare each availability group before its replica goes: move the primary, keep enough synchronized copies, and checkpoint the databases. It never blocks a reboot.

This page describes what the operator does today: it observes the signals, and its eviction webhook can delay the eviction of an availability group replica while its departure would cost a copy. The switch `operator.nodeMaintenance.mode` is `Shadow` by default: signals and verdicts are published, and nothing is acted on.

## Signals

| Signal | Emitted by | Scope |
|---|---|---|
| `Eviction` | an eviction request: `kubectl drain`, `talosctl upgrade`, `talosctl reboot --drain`, kured, Cluster API, Karpenter, cluster-autoscaler (pod condition `DisruptionTarget` with reason `EvictionByEvictionAPI`) | pod |
| `Deletion` | `kubectl delete pod`, a forced drain (`deletionTimestamp`) | pod |
| `KubeletTermination` | the kubelet's graceful node shutdown, node pressure (`DisruptionTarget`, `TerminationByKubelet`) | pod |
| `Disruption` | another `DisruptionTarget` reason: taint manager, scheduler preemption | pod |
| `Cordon` | `kubectl cordon`, any drain tool (`spec.unschedulable`) | node |
| `Taint` | `karpenter.sh/disrupted`, `karpenter.sh/disruption`, `ToBeDeletedByClusterAutoscaler`, `DeletionCandidateOfClusterAutoscaler`, `node.kubernetes.io/out-of-service`, and the keys listed in `operator.nodeMaintenance.disruptionTaints` | node |
| `NodeShutdown` | the kubelet's graceful node shutdown (`Ready=False`, "node is shutting down") | node |

A pod-scoped signal concerns the pod alone; a node-scoped signal concerns every SQL Server pod of the node.

The operator reads these signals from its watches of the SQL Server pods and of the nodes, which run on the leader. A reconcile reads them from memory, without any API call. The nodes are read only: the operator never writes on a node.

## What the operator publishes

For an availability group or a `SqlInstance` whose pods carry a signal:

- `status.maintenance` lists each pod with its node, the kinds of signals seen and when the operator first saw one, along with the mode in force;
- the condition `NodeMaintenance` is `True`, with reason `SignalsObserved` in `Shadow`;
- an event `MaintenanceSignal` is published for each new signal, and `MaintenanceEnded` once no pod carries a signal any more, when the condition goes back to `False` (`NoSignal`);
- the gauge `mssqloperator_maintenance_in_progress` is 1.

For a pod whose eviction was requested, `status.maintenance` also gives `requestedAt`, `requestedBy`, `deadline`, and the verdict of the eviction webhook (`verdict`, `reason`); see below.

A resource whose pods never carried a signal gets neither the field nor the condition: nothing is written in steady state. After a restart, the operator draws no conclusion until it has listed the pods and the nodes, so that a maintenance in progress is not cleared by a leader that has not looked yet.

## Mode

| Mode | Effect |
|---|---|
| `Off` | no observation |
| `Shadow` (default) | signals published as above; nothing is acted on |
| `Enforce` | the eviction webhook delays the replicas of an availability group, and the primary is failed over before it leaves, as described below |

The annotation `mssql-operator.evariops.com/node-maintenance` on a resource lowers the mode for that resource (`Off` or `Shadow`); it cannot raise it above the operator's.

## Eviction webhook

The chart registers a validating webhook on `pods/eviction` while the mode is not `Off`. Every replica of the operator serves it. It is configured with `failurePolicy: Ignore` and a 3-second timeout, whatever `webhook.failurePolicy` says: when the operator is down, slow or unreachable, evictions go on at once. Every eviction of the cluster goes through it; `operator.nodeMaintenance.evictionWebhook.namespaceSelector` limits it to the namespaces of the SQL resources.

For each eviction, the webhook reads the pod. A pod the operator does not manage is allowed at once. For a pod of an availability group or of a `SqlInstance`, the webhook records the request on the pod, except in a dry-run:

| Annotation | Value |
|---|---|
| `mssql-operator.evariops.com/disruption-requested-at` | when the eviction was first asked |
| `mssql-operator.evariops.com/disruption-requested-by` | who asked first; a node identity (`system:node:<node>`) replaces another requester |
| `mssql-operator.evariops.com/disruption-last-request-at` | when it was last asked again, to within 30 s |

Drain tools retry a refused eviction every 5 s. A request not repeated for 2 minutes is abandoned: the leader removes these annotations, and the next eviction starts a new delay.

### Verdicts

The leader computes a verdict for each recorded request of an availability group, at every pass of the AG (every 5 s while a request stands), from the health read of that pass:

- **Allow** when every other replica is present, connected and, for a synchronous one, synchronized, and when no failover or rolling update is in progress.
- **Delay** otherwise, naming what it waits for: `waiting for ag-1 to be synchronized`, `waiting for ag-1 to be back`, `a rolling update in progress on the availability group`.
- **Delay** when the eviction of another replica was requested first, or another replica is already stopping (`ag-1 leaves first`): the replicas of an AG leave one at a time, in the order of the requests.
- **Allow** when every other replica is on a node that is about to stop (co-located replicas): waiting buys nothing.

The primary waits for a planned failover; see [Planned failover before the primary leaves](#planned-failover-before-the-primary-leaves).

A synchronous secondary that is synchronized holds every commit of the primary: stopped, it holds them until the session timeout of the replica (10 s by default), because SQL Server's shutdown does not close its HADR session. In `Enforce`, before letting such a secondary go, the leader makes it asynchronous-commit on the primary (event `AvailabilityModeLowered`, verdict `switching ag-1 to asynchronous commit before it leaves`), then allows its eviction. Once its pod is back and connected, or once its drain gave up, it is made synchronous-commit again (`AvailabilityModeRestored`); `status.maintenance.asyncHold` lists the replicas in between, and keeps them after the signals end. The next departures of the AG still wait for it to be synchronized. Under durability `Quorum`, nothing is changed: the floor then holds the writes by design.

When the leaving secondary is the last synchronized synchronous one of a `Legacy` AG, and an asynchronous replica is connected, healthy and not leaving, that replica is made synchronous-commit first (`AvailabilityModeReinforced`, verdict `making ag-2 synchronous before ag-1 leaves`), and the departure waits until it is synchronized: the AG keeps a second synchronized copy and a lossless failover target through the reboot. Once no eviction stands and the secondary is back and synchronized, the reinforced replica goes back to asynchronous commit (`AvailabilityModeReinforcementEnded`); `status.maintenance.reinforced` lists it in between. The cost is the commit latency of that replica for the time of the maintenance. `spec.maintenance.temporarySynchronousCommit: Never` turns this off; under `Quorum`, it does not apply.

In `Enforce`, the leader writes the verdict on the pod (`mssql-operator.evariops.com/disruption-verdict`, a JSON object), valid 30 s. The webhook answers:

- with a valid verdict: its decision;
- without a verdict, or with one older than its validity (no leader): HTTP 429, until the deadline;
- once the deadline is reached: allow, and a `MaintenanceDelayExpired` warning event.

The message of a 429 reaches the drain tool, for example:

```
evicting pod prod/ag-2 (will retry after 5s): mssql-operator, availability group prod/ag: waiting for ag-1 to be synchronized; delayed 12 s of 600 s at most.
```

The first attempt of an eviction is always refused (`waiting for the operator's verdict`): the leader computes the verdict once the request is recorded, and the next attempt, 5 s later, gets it.

In `Shadow`, the verdicts are computed and published in `status.maintenance`, in the events `EvictionAllowed` and `EvictionDelayed` and in the operator logs; the webhook records the requests and allows every eviction. Events of the same reason on one resource are merged within 30 s, as for every event of the operator: `status.maintenance` lists every pod.

A `SqlInstance` is never delayed: its requests are recorded and reported, and it stops for the maintenance.

### Planned failover before the primary leaves

When the eviction of the primary is requested, or when its node is cordoned or tainted for removal (before any eviction), the leader asks for a planned failover in `Enforce`, through `status.maintenance.failoverTarget`, and the failover orchestrator runs it on the next pass, within a second: the same path as the `failover-to` annotation, with its checks (lossless target, version, log positions with `operator.plannedFailoverLsnCheck`), recorded with the reason `NodeMaintenance`. Meanwhile the verdict of the primary is `planned failover to ag-1 before the primary leaves`; once it is a secondary, its eviction follows the rules of a secondary.

The target is a replica that is ready, synchronized, in synchronous commit, not seeding, and without a signal of its own. A replica on the same node is a target when only the primary's pod is evicted, not its node. Among the targets, the smallest redo wins (to the second, from `redo_queue_size` and `redo_rate`), then `failover.preferredTargets`, then the pod name. While the redo of the best target exceeds 5 s, the failover waits with the primary still serving (`waiting for the redo of ag-1 to catch up`): after a promotion, the databases take writes only once the redo is replayed.

There is one attempt per primary and per maintenance (`status.maintenance.failoverFrom`): if the failover is refused or fails, the primary is not held for another and leaves once the other replicas are synchronized; the failover is then the unplanned one. The primary is not held either when the operator does not manage the failovers (`failover.operatorManaged: false`) or when the actuation gateway would hold the failover back (`automation.mode: Plan` or `Observe`), since the failover would not run. In `Shadow`, the verdict says which failover would run, and none is asked. The event `MaintenanceFailover` says when one is asked.

### Deadlines

A delay never exceeds its deadline, counted from the first request:

| Requester | Default | Value |
|---|---|---|
| a node identity (`system:node:*`): the drain of `talosctl upgrade` | 210 s | `operator.nodeMaintenance.evictionDeadlineSeconds.node` |
| any other: `kubectl drain`, kured, Cluster API, autoscalers | 600 s | `operator.nodeMaintenance.evictionDeadlineSeconds.other` |

The drain of `talosctl upgrade` gives up after 5 minutes and then forces a 60-second grace period: 210 s leave the orderly shutdown its time before Talos forces it. `kubectl drain` waits for ever by default; with `--timeout`, give it more than the deadline, or the pod ends in the unplanned path. Neither value goes below 30 s.

The histogram `mssqloperator_eviction_delay_seconds` measures each delay, from the first request to the answer that let the eviction go, with the label `outcome`: `verdict`, `deadline` or `release`.

### Releasing a delay

Any value of the annotation `mssql-operator.evariops.com/maintenance-release` on the availability group releases its evictions at once:

```bash
kubectl annotate sqlavailabilitygroup <ag> -n <ns> mssql-operator.evariops.com/maintenance-release=now
```

The leader removes it once no eviction request is left on the pods of the AG.

### PodDisruptionBudget

Each availability group has a PodDisruptionBudget `<ag>-pdb` with `maxUnavailable: 1` and `unhealthyPodEvictionPolicy: AlwaysAllow`. It is what limits an AG to one replica away at a time when the operator is not running (parallel drains, autoscalers), and a replica that is not ready never holds a drain.

With one replica already away, `maxUnavailable: 1` retains every other replica, even one the webhook lets go (co-located replicas on a node that stops, a deadline reached). In `Enforce`, the leader then raises `maxUnavailable` to 1 plus the replicas away, so that the released replica can leave, and sets it back to 1 once no eviction request is left; each change publishes `PodDisruptionBudgetChanged`. In `Shadow` and `Off`, the budget stays at 1. If the operator stops while the budget is raised, it stays raised until the operator runs again.

### Without the operator

| Situation | Effect |
|---|---|
| no replica of the operator runs | the webhook does not answer: evictions go on at once |
| the operator runs, without a leader | no verdict is refreshed: evictions are delayed until their deadline at most |
| an API call of the webhook fails or exceeds 1 s | the eviction is allowed |
| the request cannot be recorded on the pod | the eviction is allowed |

## Stops without an eviction

A pod deleted (`kubectl delete pod`), or stopped by the kubelet's graceful node shutdown (`talosctl reboot` without `--drain`, a node pressure), goes through no eviction: no webhook can delay it, and its grace period is already running. In `Enforce`, when this pod is the primary of an availability group, the leader asks for the planned failover at once, without waiting for the redo of the target. Meanwhile the `preStop` hook of the primary waits, before its `CHECKPOINT`, while the replica is still the primary and a synchronous secondary is connected and healthy, at most `shutdown.primaryHandoffSeconds`:

- 30 s by default, 0 to never wait, at most 300;
- never closer than 45 s to `shutdown.gracePeriodSeconds`, which the `CHECKPOINT` (15 s) and SQL Server's shutdown need;
- armed by the operator through a marker in `master` of each replica (extended property `mssql-operator.primary-handoff-seconds`), set only while it will fail over (`Enforce`, failovers managed by the operator, gateway in `Apply`). Without the marker, after a mode change, or when the operator is gone, nothing waits.

Once demoted, the replica checkpoints and stops as a secondary. A pod the operator deletes itself (rolling update, fencing) starts no failover.

The wait has to fit in the time the stop leaves: an eviction overrides the grace period (60 s under `talosctl upgrade`), and the kubelet's graceful node shutdown gives the pods `shutdownGracePeriod` minus `shutdownGracePeriodCriticalPods`. With a shorter budget, the kubelet kills SQL Server before its `CHECKPOINT`, and the next startup replays a crash recovery. For a node shutdown, give the kubelet `shutdownGracePeriod: 240s` and `shutdownGracePeriodCriticalPods: 30s`.

## Restarting fast

A replica or an instance that comes back from a reboot is ready once its startup probe (TCP on 1433) and its readiness probe (a `sqlcmd` login, and every user database out of recovery) have passed. Both run every 10 s by default, which can add up to two periods to each return. `spec.probes.startupPeriodSeconds` and `spec.probes.readinessPeriodSeconds` (from 1 to 10 s) shorten them, at the cost of one TCP connection or one `sqlcmd` login per period. The failure thresholds stay counted in 10-second steps: the 15-minute startup budget of a cumulative update and the 1-minute tolerance of a slow instance do not change. Changing a period rolls the pods once.

## Simulating a maintenance without rebooting

Every signal can be produced with a standard Kubernetes command, which is how the behaviour is tested on clusters where a real reboot is not possible:

```bash
# Node-scoped: cordon the node of a replica, then remove the cordon.
kubectl cordon <node>
kubectl uncordon <node>

# Node-scoped: a disruption taint.
kubectl taint node <node> node.kubernetes.io/out-of-service=:NoSchedule
kubectl taint node <node> node.kubernetes.io/out-of-service-

# Pod-scoped: an eviction through the Eviction API (the pod is deleted if nothing delays it).
kubectl create --raw /api/v1/namespaces/<ns>/pods/<pod>/eviction -f - <<'EOF'
{"apiVersion":"policy/v1","kind":"Eviction","metadata":{"name":"<pod>","namespace":"<ns>"}}
EOF

# The same eviction as the drain of talosctl upgrade sends it, under the identity of the pod's node
# (needs the right to impersonate; NodeRestriction refuses a pod of another node).
kubectl create --raw /api/v1/namespaces/<ns>/pods/<pod>/eviction \
  --as=system:node:<node> --as-group=system:nodes -f - <<'EOF'
{"apiVersion":"policy/v1","kind":"Eviction","metadata":{"name":"<pod>","namespace":"<ns>"},"deleteOptions":{"gracePeriodSeconds":60}}
EOF

# What the webhook would answer, without any effect.
kubectl drain <node> --pod-selector mssql-operator.evariops.com/ag=<ag> --dry-run=server

# Pod-scoped: a deletion.
kubectl delete pod <pod> -n <ns>
```

Cordoning a node that runs every replica of an AG concerns them all: on a cluster with a single node of the right architecture, prefer the pod-scoped signals.
