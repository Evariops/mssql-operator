# Safety mechanisms

The previous chapter covers failures around the operator. This one covers failures of the operator itself. An operator managing production databases holds enough privileges to destroy them, and it can be wrong in several ways: a bug, a decision taken on a stale view of the state, a worker blocked on a frozen server, a partial view of the replicas, two processes acting as leader after one of them was paused, or state lost because a resource was re-created or etcd was restored.

Each of these cases has a mechanism that limits the damage. Some are always active. The more recent ones are grouped in [safety levels](#safety-levels), and each level first runs in shadow mode, where its decisions are computed and logged without being applied, so that they can be compared with the active path before being enabled.

## Actuation gateway

Every action that interrupts service or loses data is a typed request to a single component, the [`ActuationGateway`](../../src/MsSqlOperator/Services/Actuation/ActuationGateway.cs). The methods that perform these actions carry a `[Destructive]` attribute, and the architecture test `DestructiveCallsGoThroughTheGatewayTests` fails the build when one of them is called from anywhere else. A new code path therefore cannot reach a `DROP` or a promotion without going through the gateway.

The [action catalog](../../src/MsSqlOperator/Services/Actuation/Actions.cs) sorts actions into two classes. Disruptive actions interrupt service without losing anything: a failover, a replica demotion, the deletion or forced deletion of a pod, the deletion of a VolumeAttachment, the re-seed of a copy, killing sessions, disabling a login, moving a replica to synchronous commit. Destructive actions lose something that exists nowhere else, or cannot be undone: a failover with data loss, resuming a divergent replica, removing a database from an AG, dropping a database, a login, a database user or a procedure, deleting a job, generating a machine key, and regenerating the service master key.

For each request, [`ActuationPolicy`](../../src/MsSqlOperator/Services/Actuation/ActuationPolicy.cs) looks first at the automation mode of the resource, `spec.automation.mode`, capped by the operator-wide ceiling `Operator:Automation:MaxMode`. In `Plan` and `Observe`, nothing disruptive or destructive runs, and the decision is recorded as if it had. In `Apply`, disruptive actions run, and destructive actions run without approval only in a few cases: when they clean up a resource being deleted, when the spec requests them (a database removed from a `SqlUser`, `allowAutomaticDataLossFailover`), when a dedicated annotation requests them, or when their type is listed in `spec.automation.preAuthorized`, within a budget of one action per resource per hour and three per hour for the whole operator. Any other destructive action waits for the annotation `mssql-operator.evariops.com/approve=<action id>`. In every case, the preconditions of the action are checked again immediately before it runs, not when it was planned.

Each decision is recorded in the plan (`kubectl get operatorplans`, held in the leader's memory), published as an event (`ActionPlanned`, `ActionRequiresApproval`, `ActionDenied`) and counted in `mssqloperator_actuation_decisions_total`. An action id has the form `<type>:<target>:<epoch>:<fingerprint>`. It is deterministic, so an approval applies to one specific action, and a different action on the same target needs its own approval.

Multi-step operations need one more rule: they are decided by their last step. A failover demotes or fences the old primary before it promotes the target, and if the promotion were withheld after the demotion, the AG would be left without a primary. Such an operation therefore asks the gateway whether its last step would be allowed (`WouldRunAsync`) before running the first one, and does nothing if not. The chaos campaigns found the problem this rule solves. In `Plan` mode, a planned failover removed the writer label and demoted the primary, then the promotion was withheld, and so was the rollback, which is itself a promotion; the AG stayed without a primary for twelve minutes, in a mode that is supposed to change nothing. In the same mode, the fencing deleted the old primary's pod thirteen times in 45 seconds, because the gateway evaluated the pod deletion against the pod, which has no automation mode, and therefore applied the operator ceiling, `Apply`. Both are fixed.

Two actions are never automatic, whatever the settings, because nothing can undo them: regenerating the service master key, and generating a machine key over existing data. The test `ForbiddenStatementsTests` additionally scans the source for statements the operator must never send, such as `FORCE REGENERATE`.

## Control record and intents

A failover is a sequence of effects spread over several reconciles: removal of the writer label, fencing, promotion, label move. The operator can stop between any two of them, and its successor must know what was in progress and how far it went; otherwise it would decide again from scratch, possibly with a different target.

The rule is to persist the intent before the first effect. Before acting, a failover writes its intent in the AG status (`status.failoverIntent`, plus `status.fencing` during the fencing stage). A reconcile that finds an open intent settles it before taking any other decision: it completes it if the target became primary, and abandons it otherwise. If the intent cannot be written, the reconcile does nothing at all.

The status belongs to the resource, though, and is lost with it. A GitOps tool that deletes and re-creates an AG produces an object with a new UID and an empty status, while the replicas, the volumes and the failover history still exist. The state that must survive the resource is therefore kept in a separate object, the [`SqlControlRecord`](../reference/resources.md#sqlcontrolrecord), one per AG (`ag-<name>`) and one per instance (`instance-<name>`). It has no owner reference and is deleted only once both the resource and its volumes are gone. It holds the operator epoch, the failover intent in progress with its stage, the durability profile once reached, the open divergences left by failovers with data loss, and the fingerprint of the machine key the data was created with.

The API server itself enforces two rules on this object, through CEL rules in [its CRD](../../charts/mssql-operator/templates/crds/sqlcontrolrecords_mssql-operator_evariops_com.yaml): the epoch never decreases, and an intent in progress can only be replaced by a writer with a strictly higher epoch. A control record that is lost, or deleted by hand, is rebuilt from SQL Server and from the status and marked with `reconstructedAt`, and no destructive action runs until the rebuilt record has been written.

## Operator epoch

The Kubernetes Lease elects one leader, but it cannot stop a former one. A process paused by a long garbage collection, a frozen node or `SIGSTOP` may still hold an open SQL connection when it resumes, and send one more command after a new leader has taken over. Exiting when the Lease is lost narrows this window but cannot close it, because the paused process runs no code at all until it resumes. The epoch closes it on the SQL Server side, where the commands land:

1. Each new leader takes an epoch before reconciling anything. It reads the counter of the Lease `mssql-operator-epoch` and the highest epoch found in the control records, and writes the maximum plus one with a compare-and-swap ([`OperatorEpoch`](../../src/MsSqlOperator/Services/Control/OperatorEpoch.cs)). Two leaders never hold the same epoch.
2. The topology commands of the gateway run in a single session, in four steps: take the exclusive application lock `mssqlop:<scope>` (`sp_getapplock`, 5-second timeout, error 51001 on failure); read the stamp in `msdb.mssqlop.epoch` and refuse the command with error 51002 if the stamp is higher than the author's epoch; write the author's stamp; run the command. The stamped commands are the promotion (`FORCE_FAILOVER_ALLOW_DATA_LOSS`), the removal of a replica or of a database from the AG, the drop of a secondary's copy or of a replica's local AG, and the change of a replica to synchronous commit ([`Actuators`](../../src/MsSqlOperator/Services/Actuation/Actuators.cs), [`MssqlopSchema`](../../src/MsSqlOperator/Services/SqlServer/MssqlopSchema.cs)).
3. A command from a former leader therefore reaches the replica after its successor's stamp, and SQL Server refuses it.

The stamps also make the epoch robust to an etcd restore. A restore can move the Lease counter and the control records back in time, but it does not touch the stamps in `msdb`. A leader that finds a stamp above its own epoch raises its epoch above that stamp and abandons the current reconcile.

## Safety levels

The control record, the epoch and the SQL-side exclusion were added while the operator was already running real availability groups, and enabling them everywhere at once would have been a risk in itself. They are therefore grouped in levels, each one including the previous ones ([`SafetyLevel`](../../src/MsSqlOperator.Core/Safety/SafetyLevel.cs)):

| Level | Content |
|---|---|
| 0 | the original logic, with the fixes from the chaos campaigns |
| 1 | the control record, the epoch, the stamps and locks on the SQL Server side |
| 2 | the quorum watchdog, elections by quorum and listener routing by pod name: the protocol described in the next section, which does not use fencing |
| 3 | event-driven supervision |

At level 3, [`AgSupervisor`](../../src/MsSqlOperator/Services/Health/AgSupervisor.cs) runs one actor per AG, which reads the AG state on its own schedule, coalesces bursts of signals into one evaluation, and feeds the state store whose transitions wake the database controllers. Below level 3, the AG controller's 10-second reconcile performs the state read.

An AG runs at the level of its annotation `mssql-operator.evariops.com/safety-level`, 0 when the annotation is absent, capped by the Helm value `operator.safetyLevelMax` (1 by default). The level just above the effective one runs in shadow mode, within the ceiling: its decisions are computed next to the active path and logged, which allows both to be compared on real workloads before switching. Each transitional setting also declares the version by which its old code path must be removed (`TransitionalSettings`), and a test fails the build once that version is reached, so that the switches do not become permanent by neglect.

Level 1 is the recommended setting. Level 2 is not safe to enforce yet, for the reason explained at the end of the next section.

## Level 2 protocol (experimental)

Fencing has a weak point: it depends on Kubernetes, and on a lost node it depends on storage that can confirm a detachment. Level 2 is a failover protocol that needs neither. It is specified by invariants, checked by a simulation, and partly enforced by SQL Server itself. Its main invariants are:

- I1: at most one replica can acknowledge commits, and the listener routes only to it;
- I2: no acknowledged commit is lost without an explicit and recorded human decision;
- I4: every irreversible effect is preceded by its intent, persisted with the epoch of its author, and a stale author is refused;
- I9: no decision depends on the content or the order of an event, and a lost stream triggers a full re-read;
- I10: the old primary is offline before the new one accepts a write;
- I11: the absence of the operator alone never degrades the data plane.

The first mechanism is the quorum watchdog, a SQL Agent job named `mssqlop-watchdog-<ag>` that runs on every replica every *J* seconds and reads nothing from the operator ([`QuorumWatchdog`](../../src/MsSqlOperator/Services/SqlServer/QuorumWatchdog.cs)). On the primary, when fewer than *k* secondaries have been synchronized for *T_step*, measured with its own clock, it takes the AG `OFFLINE`, under the same application lock as the operator's commands. On a secondary, it records when the primary was lost. A primary cut off from its secondaries thus steps down on its own, whether or not the operator sees anything.

The second mechanism is the election. A secondary is promoted only when no reachable secondary still sees the primary, when enough secondaries are isolated that no acknowledged commit can be missing from all of them, and when the old primary is known to be out. The old primary counts as out when it is observed `OFFLINE`, when its SQL container is reported terminated by a kubelet whose node Lease was renewed after that report, or when its watchdog was running and every secondary has been disconnected from it for at least *T_vote*.

The reasoning behind I2 is a counting argument. With *N* synchronous secondaries, of which *V* are isolated (data movement suspended and hardened LSNs read), every acknowledged commit is hardened on at least *k* of them. If *V* + *k* > *N*, the isolated set intersects the set that holds any given acknowledged commit, so the most advanced isolated replica holds every acknowledged commit. Fewer than *k* secondaries remain outside the isolated set, so the old primary cannot acknowledge anything more, and the set of acknowledged commits is frozen before the promotion.

I10 comes from timing. Each isolated secondary has lost the primary for at least *T_vote*. The primary notices that it lost its quorum within the session timeout *S*, and its watchdog takes it offline within *T_step* + *J* after that. With a margin for clock drift, the condition T_vote ≥ T_step + J + S + margin guarantees that the step-down happens before the promotion. [`TimingBudget`](../../src/MsSqlOperator.Core/Timing/TimingBudget.cs) checks the condition at startup, and the operator refuses to start if it does not hold. With the defaults (*S* = 10 s, *J* = 10 s, *T_step* = 20 s, a 5-second margin), *T_vote* is 45 seconds.

That proof assumes that the watchdog actually runs every *J* seconds, and the chaos campaign showed that it does not during a real partition. SQL Agent resolves DNS before each job step, which took about 6 seconds per run, and runs came every 20 seconds instead of 10. The worst-case step-down then reached about 46 seconds, against a *T_vote* of 45 seconds, so the guarantee no longer held. Before level 2 can be enforced, *T_vote* has to be raised to about 60 seconds or the watchdog moved out of SQL Agent, and the simulation re-run with the measured period. The same campaign found that lowering the ceiling from 2 to 1 left the watchdog jobs enabled, so a watchdog provisioned in enforce mode would have kept taking AGs offline with level 2 disabled; this is fixed.

## Decision core and simulation

The election protocol, the timing budget and the supervisor logic live in [`MsSqlOperator.Core`](../../src/MsSqlOperator.Core/), without I/O. `ElectionCore.Decide` is a pure function: the same observed state always yields the same decision, whatever the order, duplication or loss of the events that led to it, which is invariant I9 in practice.

Being pure, the core can be simulated. [`AgSimulator`](../../tests/MsSqlOperator.Tests/Core/Simulation/AgSimulator.cs) models an availability group in virtual time, with replicas, the links between them, the commit rule, the watchdog of each replica, and the operator applying the election core. It injects partitions, crashes, pauses and delays from a seed, and checks I1, I2, I10 and I11 at every step. Every build runs 200 fixed seeds with three or five replicas, `MSSQLOP_SIM_SEEDS=<n>` runs additional random seeds, and a violation reports the seed that reproduces it exactly.

The simulation validates the protocol, not the behaviour of SQL Server. It did not reveal the watchdog delay described above, which was only found on a real cluster; both kinds of testing are needed.

## Ownership registry

A resource deleted and re-created by a GitOps tool comes back with a new UID and an empty status. If the operator relied on the resource alone, it would no longer know which logins it had created, or what `max degree of parallelism` was before a `SqlConfiguration` changed it. This information is therefore stored in SQL Server, next to the objects it describes, in the schema `msdb.mssqlop` of each server ([`OwnershipRegistry`](../../src/MsSqlOperator/Services/SqlServer/OwnershipRegistry.cs), [`ConfigurationLedger`](../../src/MsSqlOperator/Services/SqlServer/ConfigurationLedger.cs)).

For logins, the registry records which `SqlUser` owns each one and which server permissions it granted. This is what prevents two `SqlUser` resources from managing the same login, and what lets a re-created `SqlUser` take its login back and revoke what it no longer grants. For configuration, it records the value of each `sp_configure` option before a `SqlConfiguration` changed it, and the trace flags it enabled, so that removing the configuration restores the server as it was. The same schema also holds the epoch stamps and the watchdog state.

Inside each database, extended properties carry the rest: `mssqlop.owner` and `mssqlop.grants` on database users, `mssqlop.owner` on a database to name the `SqlDatabase` that owns it, and `mssqlop.divergence` after a failover with data loss. Because they are stored in the database, these properties are replicated with it and follow it through failovers. The schema is created on a server the first time an action targets that server, and it is versioned.

## Key material handling

SQL Server's keys decide whether the data on the volumes can be read at all ([Key material](../guide/protecting-data.md#key-material)), so the operator handles them with specific rules.

Service master key backups and TDE exports never touch the node's disk. SQL Server writes them to an in-memory volume, `/var/opt/mssqlop-keys`, from which the operator reads them back, stores them in Secrets and deletes them. On a pod that has this volume but whose SQL Server does not see it yet, no backup is taken at all; an earlier version wrote the backup to `/tmp` on the node in that case.

The key Secrets are immutable and have no owner reference. They cannot be overwritten by mistake, and they survive the deletion of the resource, so that re-creating the resource keeps the data readable. A deleted key Secret is detected by the watch and recreated from SQL Server. The SHA-256 fingerprint of the machine key the data was created with is kept in the control record, so a replaced machine key Secret raises `MachineKeyChanged` before the next restart would make the data unreadable.

The database master key has its own password and is never protected by `sa`, so an `sa` rotation cannot break TDE. A DMK created by an older version and still protected by `sa` gets the new password added and verified before the `sa` protection is removed. Finally, TDE certificates are never dropped, since a backup taken under a certificate needs that certificate to be restored.

## Isolation between servers

A single operator manages every SQL Server in the cluster, so an unresponsive server must cost seconds and must not consume the operator's capacity.

Each connection is opened with the profile of its purpose ([`SqlProfile`](../../src/MsSqlOperator/Services/ConnectionFactory/SqlProfile.cs)). Health reads connect within 3 seconds without retry and run each command within 5 seconds, DDL fanned out to every server of a target uses no retry, and HADR commands set `LOCK_TIMEOUT 5000` and `XACT_ABORT ON`. A dead replica costs one short timeout and is retried at the next reconcile. In addition, at most two reconciles of any kind work on the same instance or AG at a time, so that users, jobs, configurations and TDE cannot all pile up on the one server that is slow.

With `operator.targetCircuitBreakers: Enforce` (`Shadow` by default), the operator goes one step further: a connection to a server that the last health read could not reach fails immediately, instead of after about 2.6 minutes of timeouts and retries, and the mark is cleared as soon as the server answers again.

Operations that last longer than a reconcile, such as the initialization backup of a large database, run in a bounded background pool ([`LongOperations`](../../src/MsSqlOperator/Services/SqlServer/LongOperations.cs)); the reconcile only starts and follows them. After an operator restart, the next reconcile checks that SQL Server is no longer running the operation before starting it again. And every reconcile has a timeout, 2 minutes for an AG, whose reconcile is its health check.

## SQL injection

SQL identifiers and literals built from a spec go through quoting helpers ([`SqlQuoting`](../../src/MsSqlOperator/Services/SqlServer/SqlQuoting.cs)), and identifiers are also validated against a restricted character set at admission ([`SqlIdentifierRules`](../../src/MsSqlOperator/Services/SqlServer/SqlIdentifierRules.cs)). Two tests keep it that way: `SqlInterpolationGuardTests` fails the build when SQL-building code interpolates a value into an identifier or a literal without these helpers, and `SqlTextLivesInManagersTests` keeps T-SQL inside the manager classes, where these rules apply.

## Not covered

These mechanisms have known limits. The operator connects to SQL Server as `sa`, since a dedicated least-privileged login is not implemented, so control of the operator means control of every server it manages; only stored procedures are deployed through a restricted login (`db_ddladmin` on the targeted databases). The plans are held in memory, so `kubectl get operatorplans` only shows the decisions of the current leader and a restart empties the list, while the events remain. Level 2 is not safe to enforce, for the timing reason described above. And the gateway relies on its catalog: an action that is not classified as disruptive or destructive is not gated, and the architecture test only checks that the classified methods are not called from outside the gateway.
