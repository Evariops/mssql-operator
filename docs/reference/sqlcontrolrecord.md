# SqlControlRecord

Written by the operator, never by you. One record per availability group (`ag-<name>`) and per
instance (`instance-<name>`), in the same namespace. It keeps the safety state that must survive the
deletion and re-creation of the resource (GitOps), so it has no owner reference.

```console
$ kubectl get sqlcontrolrecords -n sql
NAME        OWNER    EPOCH   INTENT   STAGE   AGE
ag-orders   orders   12                       30d
```

| Field | Holds |
|---|---|
| `spec.epoch` | Increases at each operator leadership change; SQL writes carry it, so an operator that lost leadership cannot act any more. |
| `spec.intent` | The operation in progress (failover, replica removal, …), its stage and deadline. Empty between operations. |
| `spec.durability` | The durability profile (`Quorum` / `Legacy`), fixed once reached. |
| `spec.openDivergences` | Data-loss failovers whose diverged replicas have not been resolved. |
| `spec.removedReplicas` | Replicas the operator took out of the AG. |
| `spec.keys.machineKeySha256` | Fingerprint of the machine key the data was created with. |

It is deleted when the resource and its volumes are gone. Do not edit or delete it by hand: losing it
makes the operator rebuild it from SQL Server, in observation only, until it is consistent again.

More: [Safety internals](../internals/safety.md).
