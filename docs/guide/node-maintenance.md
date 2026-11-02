# Node maintenance

Node upgrades and reboots stop the SQL Server pods of the node. The operator watches for the signals that announce such a stop, so that it can prepare each availability group before its replica goes: move the primary, keep enough synchronized copies, and checkpoint the databases. It never blocks a reboot.

This page describes what the operator observes today. The switch `operator.nodeMaintenance.mode` is `Shadow` by default: signals are published, and nothing is acted on.

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

A resource whose pods never carried a signal gets neither the field nor the condition: nothing is written in steady state. After a restart, the operator draws no conclusion until it has listed the pods and the nodes, so that a maintenance in progress is not cleared by a leader that has not looked yet.

## Mode

| Mode | Effect |
|---|---|
| `Off` | no observation |
| `Shadow` (default) | signals published as above; nothing is acted on |
| `Enforce` | reserved for the actions of the following releases (eviction delays, planned failover before the stop); behaves as `Shadow` for now |

The annotation `mssql-operator.evariops.com/node-maintenance` on a resource lowers the mode for that resource (`Off` or `Shadow`); it cannot raise it above the operator's.

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

# Pod-scoped: a deletion.
kubectl delete pod <pod> -n <ns>
```

Cordoning a node that runs every replica of an AG concerns them all: on a cluster with a single node of the right architecture, prefer the pod-scoped signals.
