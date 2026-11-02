using System.Collections.Concurrent;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Maintenance;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.Maintenance;

/// <summary>
/// On the leader, the verdicts on the evictions the webhook recorded on the pods of a
/// resource, computed by <see cref="EvictionVerdicts"/> from the health pass of this reconcile (a
/// fresh observation) and the signals of <see cref="MaintenanceSignals"/>.
/// <list type="bullet">
/// <item>Enforce: each verdict is written on its pod (<c>disruption-verdict</c>), valid 30 s, and
/// rewritten when it changes or half of its validity is gone.</item>
/// <item>Shadow: the verdict is computed and published (status, events, logs), never written on the pod.</item>
/// </list>
/// A request abandoned by its drain (not repeated for 2 min) is cleared from its pod. Nothing is
/// decided before the pod and node caches are synchronized.
/// </summary>
public sealed class EvictionVerdictPublisher(
    MaintenanceSignals signals,
    MaintenanceObserver observer,
    OperatorOptions options,
    IKubernetesClient client,
    IEventPublisher eventPublisher,
    TimeProvider timeProvider,
    ILogger<EvictionVerdictPublisher> logger,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null)
{
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    /// <summary>The last verdict reported per pod (namespace/pod), to publish an event when it changes.</summary>
    private readonly ConcurrentDictionary<string, (VerdictDecision Decision, string Reason)> _reported = new(StringComparer.Ordinal);

    /// <summary>The last verdict written on each pod (Enforce), to rewrite it when it changes or ages.</summary>
    private readonly ConcurrentDictionary<string, PublishedVerdict> _published = new(StringComparer.Ordinal);

    /// <summary>The maxUnavailable each AG's PDB should have, and the one last applied.</summary>
    private readonly ConcurrentDictionary<string, int> _pdbDesired = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _pdbApplied = new(StringComparer.Ordinal);

    /// <summary>The PDB's maxUnavailable outside any widening.</summary>
    public const int PdbMaxUnavailable = 1;

    private void Forget(string key)
    {
        _reported.TryRemove(key, out _);
        _published.TryRemove(key, out _);
    }

    private static string AgKey(SqlAvailabilityGroup ag) => $"{ag.Metadata.NamespaceProperty}/{ag.Metadata.Name}";

    /// <summary>The maxUnavailable the PDB of <paramref name="ag"/> should have now —
    /// 1, or wider while the webhook lets a ready replica go that the PDB would retain.</summary>
    public int PdbBudget(SqlAvailabilityGroup ag) => _pdbDesired.GetValueOrDefault(AgKey(ag), PdbMaxUnavailable);

    /// <summary>True when the PDB must be applied with <paramref name="maxUnavailable"/>: the budget
    /// changed since it was last applied (a pass applies it at most once per change).</summary>
    public bool TakePdbChange(SqlAvailabilityGroup ag, out int maxUnavailable)
    {
        var key = AgKey(ag);
        maxUnavailable = PdbBudget(ag);
        if (_pdbApplied.GetValueOrDefault(key, PdbMaxUnavailable) == maxUnavailable)
            return false;
        _pdbApplied[key] = maxUnavailable;
        return true;
    }

    /// <summary>The infrastructure pass applied the PDB with <paramref name="maxUnavailable"/>.</summary>
    public void PdbApplied(SqlAvailabilityGroup ag, int maxUnavailable) => _pdbApplied[AgKey(ag)] = maxUnavailable;

    /// <summary>
    /// The PDB counts a replica that is not ready as disrupted: with <c>maxUnavailable: 1</c>, one
    /// replica away retains every other one. When the webhook lets a ready replica go (its verdict
    /// allows it, its deadline is reached, co-located replicas), the budget becomes 1 plus the replicas
    /// away, so that exactly that one can leave; back to 1 once no request is left. Enforce only: in
    /// Shadow the PDB stays as it is.
    /// </summary>
    internal static int Budget(IReadOnlyList<ReplicaView> replicas, IEnumerable<(string Pod, Verdict Verdict)> verdicts, DateTimeOffset now)
    {
        var away = replicas.Count(r => !r.Ready);
        var released = verdicts.Any(v => (v.Verdict.Decision == VerdictDecision.Allow || now >= v.Verdict.Deadline)
                                         && replicas.Any(r => r.Pod == v.Pod && r.Ready));
        return released ? Math.Clamp(away + 1, PdbMaxUnavailable, Math.Max(PdbMaxUnavailable, replicas.Count)) : PdbMaxUnavailable;
    }

    /// <summary>Verdicts for the replicas of <paramref name="ag"/>. <paramref name="health"/> null: the AG is
    /// not running yet, and nothing holds its evictions. Fills <c>status.maintenance</c>; true while
    /// eviction requests stand, so that the AG is reconciled again within seconds.</summary>
    public async Task<bool> PublishAsync(SqlAvailabilityGroup ag, AgHealthReport? health, string? operationInProgress, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var requests = await LiveRequestsAsync(ag, "SqlAvailabilityGroup", ct);
        if (requests is null || requests.Count == 0)
            _pdbDesired.TryRemove(AgKey(ag), out _);
        if (requests is null)
            return false;

        var mode = observer.ModeOf(ag);
        var now = timeProvider.GetUtcNow();
        var budget = options.EvictionBudget;
        var key = ns is null ? ag.Metadata.Name : $"{ns}/{ag.Metadata.Name}";
        var signalsByPod = signals.For("SqlAvailabilityGroup", key)
            .ToDictionary(p => p.Pod, p => p.Signals, StringComparer.Ordinal);
        var replicas = health is null ? [] : ReplicasOf(ag, health);
        var preferred = ag.Spec.Failover?.PreferredTargets?.Select(p => $"{ag.Metadata.Name}-{p.Ordinal}").ToList();
        var plannedFailover = health is not null && FailoverAvailable(ag, health.PrimaryReplica);

        // The node of the primary announces its stop before any eviction (a cordon, a
        // disruption taint): the primary moves now, once, while nothing presses yet. So does
        // a primary already stopping without an eviction (pod deleted, kubelet shutdown), without
        // waiting for the redo since the grace period runs; its preStop hook waits for it.
        if (health?.PrimaryReplica is { } primary && plannedFailover
            && signalsByPod.TryGetValue(primary, out var primarySignals)
            && EvictionVerdicts.FailoverTargetFor(primary, replicas, signalsByPod, preferred) is { Target: { } early } choice)
        {
            var stopping = primarySignals.Any(s => s.Kind is SignalKind.Deletion or SignalKind.KubeletTermination or SignalKind.NodeShutdown)
                           && !OperatorDisruption(ag);
            if (stopping || (choice.Now && primarySignals.Any(s => s.Scope == SignalScope.Node)))
                await RequestFailoverAsync(ag, primary, early, mode,
                    $"{(stopping ? "it is stopping" : "its node announces a stop")} ({string.Join(", ", primarySignals.Select(s => s.Kind).Distinct())})", ct);
        }

        if (requests.Count == 0)
        {
            await ClearReleaseAsync(ag, ct);
            return false;
        }

        var all = requests.Select(r => r.Record.ToRequest(r.Pod)).ToList();
        var verdicts = new List<(string Pod, Verdict Verdict)>();

        foreach (var (pod, record) in requests)
        {
            var request = record.ToRequest(pod);
            var verdict = health is null
                ? new Verdict(VerdictDecision.Allow, "the availability group is not running yet", budget.DeadlineOf(request))
                : EvictionVerdicts.Decide(request, replicas, signalsByPod, all, operationInProgress, now, budget, plannedFailover, preferred);
            if (health is not null)
                verdict = HoldUntilAsynchronous(ag, pod, Reinforce(ag, pod, verdict, health, mode, now), health, mode, now);
            Report(ag.Status?.Maintenance, pod, record, verdict);
            await PublishVerdictAsync(ag, pod, verdict, mode, now, ct);
            verdicts.Add((pod, verdict));
            // The primary is moved before it leaves; its verdict stays delay meanwhile.
            if (verdict.FailoverTarget is { } target)
                await RequestFailoverAsync(ag, pod, target, mode, "its eviction is requested", ct);
        }

        var pdb = mode == SwitchMode.Enforce && health is not null ? Budget(replicas, verdicts, now) : PdbMaxUnavailable;
        if (pdb == PdbMaxUnavailable)
            _pdbDesired.TryRemove(AgKey(ag), out _);
        else
            _pdbDesired[AgKey(ag)] = pdb;
        return true;
    }

    /// <summary>A SqlInstance: its evictions are not held; the requests are reported in
    /// <c>status.maintenance</c> and an abandoned one is cleared. True while requests stand.</summary>
    public async Task<bool> TrackAsync(SqlInstance instance, CancellationToken ct)
    {
        var requests = await LiveRequestsAsync(instance, "SqlInstance", ct);
        foreach (var (pod, record) in requests ?? [])
            Report(instance.Status?.Maintenance, pod, record, new Verdict(VerdictDecision.Allow,
                "an instance is not held: it stops for the maintenance", options.EvictionBudget.DeadlineOf(record.ToRequest(pod))));
        return requests?.Count > 0;
    }

    /// <summary>The live requests on the pods of the resource; null when nothing may be concluded
    /// (mode Off, caches not synchronized). Abandoned requests are cleared on the way.</summary>
    private async Task<List<(string Pod, EvictionRecord Record)>?> LiveRequestsAsync(
        IKubernetesObject<V1ObjectMeta> resource, string kind, CancellationToken ct)
    {
        if (observer.ModeOf(resource) == SwitchMode.Off || !signals.IsSynced)
            return null;
        var ns = resource.Metadata.NamespaceProperty;
        var now = timeProvider.GetUtcNow();
        var live = new List<(string, EvictionRecord)>();
        foreach (var pod in signals.PodsOf(kind, ns is null ? resource.Metadata.Name : $"{ns}/{resource.Metadata.Name}"))
        {
            if (pod.EvictionRecord is not { } record)
            {
                Forget($"{ns}/{pod.Name}");
                continue;
            }
            if (!EvictionRequests.IsAbandoned(record, now))
            {
                live.Add((pod.Name, record));
                continue;
            }
            try
            {
                await client.PatchPodAnnotationsAsync(pod.Name, ns!, EvictionAnnotations.Clear(), ct);
                Forget($"{ns}/{pod.Name}");
                logger.LogInformation("{Kind} {Namespace}/{Name}: eviction request on {Pod} abandoned (last asked {Last:O}), cleared",
                    kind, ns, resource.Metadata.Name, pod.Name, record.LastRequestAt);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "{Kind} {Namespace}/{Name}: could not clear the abandoned eviction request on {Pod}",
                    kind, ns, resource.Metadata.Name, pod.Name);
            }
        }
        return live;
    }

    /// <summary>
    /// Whether the operator will fail <paramref name="primary"/> over before it leaves.
    /// Not when it does not manage the failovers, nor when the actuation gateway would hold the failover
    /// back (Plan, Observe): holding a reboot for a failover that will not run buys nothing. One attempt
    /// per primary in a maintenance: once it ran or failed, the primary leaves like a secondary.
    /// </summary>
    internal bool FailoverAvailable(SqlAvailabilityGroup ag, string? primary)
    {
        if (primary is null || (ag.Spec.Failover?.OperatorManaged ?? true) == false || !Gateway.Acts(ag))
            return false;
        var maintenance = ag.Status?.Maintenance;
        return maintenance?.FailoverFrom is not { } from || !from.Equals(primary, StringComparison.OrdinalIgnoreCase)
               || maintenance.FailoverTarget is not null;
    }

    /// <summary>A pod the operator stops itself (rolling update, fencing) is no maintenance signal.</summary>
    private static bool OperatorDisruption(SqlAvailabilityGroup ag)
        => ag.Status?.Fencing is not null
           || ag.Status?.RollingUpdate is { Phase: not (RollingUpdatePhases.Idle or RollingUpdatePhases.Failed) };

    /// <summary>
    /// The seconds the preStop hook of a stopping primary may wait for its planned failover,
    /// written as a marker in master of each replica; null when no failover will come (not Enforce,
    /// failovers not managed by the operator, gateway holding them back, or 0): nothing then waits.
    /// Kept 45 s under the grace period, for the CHECKPOINT (15 s) and SQL Server's own shutdown.
    /// </summary>
    public int? HandoffSeconds(SqlAvailabilityGroup ag)
    {
        var shutdown = ag.Spec.InstanceTemplate?.Shutdown ?? new ShutdownSpec();
        if (observer.ModeOf(ag) != SwitchMode.Enforce || (ag.Spec.Failover?.OperatorManaged ?? true) == false || !Gateway.Acts(ag))
            return null;
        var seconds = (int)Math.Min(shutdown.PrimaryHandoffSeconds, shutdown.GracePeriodSeconds - 45);
        return seconds > 0 ? seconds : null;
    }

    /// <summary>Asks the failover orchestrator, through the status, for a planned failover of
    /// <paramref name="primary"/> to <paramref name="target"/>; it runs on the next pass, within a second.
    /// Shadow: the verdict says it; nothing is asked.</summary>
    private async Task RequestFailoverAsync(SqlAvailabilityGroup ag, string primary, string target, SwitchMode mode, string why, CancellationToken ct)
    {
        if (mode != SwitchMode.Enforce || ag.Status?.Maintenance is not { } maintenance
            || primary.Equals(maintenance.FailoverFrom, StringComparison.OrdinalIgnoreCase))
            return;
        maintenance.FailoverTarget = target;
        maintenance.FailoverFrom = primary;
        logger.LogInformation("AG {Namespace}/{Name}: planned failover of the primary {Primary} to {Target}: {Why}",
            ag.Metadata.NamespaceProperty, ag.Metadata.Name, primary, target, why);
        await eventPublisher.PublishAsync(ag, "MaintenanceFailover",
            $"Planned failover of the primary {primary} to {target} before it stops: {why}.", ct: ct);
    }

    private static bool Held(SqlAvailabilityGroup ag, string pod)
        => ag.Status?.Maintenance?.AsyncHold?.Contains(pod, StringComparer.OrdinalIgnoreCase) == true;

    private static bool Synchronous(AgHealthReport health, string pod)
        => health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase))?.AvailabilityMode == "SYNCHRONOUS_COMMIT";

    /// <summary>
    /// A synchronous secondary that is SYNCHRONIZED holds every commit of the primary while
    /// it is there; stopped, it holds them until the session timeout (10 s measured on a test cluster), SQL
    /// Server's shutdown not closing the HADR session. When its departure is allowed, it is first made
    /// asynchronous-commit on the primary, and its verdict stays delay until it is. Enforce only, with
    /// the gateway acting, before the deadline, and not under Quorum, whose floor is then what holds
    /// the writes.
    /// </summary>
    private Verdict HoldUntilAsynchronous(SqlAvailabilityGroup ag, string pod, Verdict verdict, AgHealthReport health, SwitchMode mode, DateTimeOffset now)
    {
        if (mode != SwitchMode.Enforce || verdict.Decision != VerdictDecision.Allow || now >= verdict.Deadline
            || pod.Equals(health.PrimaryReplica, StringComparison.OrdinalIgnoreCase) || !Synchronous(health, pod)
            || health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase)) is not { IsFailoverReady: true }
            || MsSqlOperator.Services.Control.DurabilityProfile.InForce(ag) == MsSqlOperator.Services.Control.DurabilityProfile.Quorum
            || !Gateway.Acts(ag) || ag.Status?.Maintenance is not { } maintenance)
            return verdict;
        if (!Held(ag, pod))
            (maintenance.AsyncHold ??= []).Add(pod);
        return verdict with { Decision = VerdictDecision.Delay, Reason = $"switching {pod} to asynchronous commit before it leaves, so that its stop does not hold the writes" };
    }

    /// <summary>
    /// When the leaving secondary is the last synchronized synchronous one, an
    /// asynchronous replica that is connected, healthy and not leaving is made synchronous first, and
    /// the departure waits until it is synchronized (the verdict then names it). Without it, the AG would
    /// keep a single copy and no lossless failover target while the node reboots. Legacy only (Quorum
    /// keeps its floor); not with temporarySynchronousCommit Never, in Shadow, past the deadline.
    /// </summary>
    private Verdict Reinforce(SqlAvailabilityGroup ag, string pod, Verdict verdict, AgHealthReport health, SwitchMode mode, DateTimeOffset now)
    {
        if (mode != SwitchMode.Enforce || verdict.Decision != VerdictDecision.Allow || now >= verdict.Deadline
            || pod.Equals(health.PrimaryReplica, StringComparison.OrdinalIgnoreCase) || !Synchronous(health, pod)
            || string.Equals(ag.Spec.Maintenance?.TemporarySynchronousCommit, "Never", StringComparison.OrdinalIgnoreCase)
            || MsSqlOperator.Services.Control.DurabilityProfile.InForce(ag) != MsSqlOperator.Services.Control.DurabilityProfile.Legacy
            || !Gateway.Acts(ag) || ag.Status?.Maintenance is not { } maintenance)
            return verdict;
        var others = health.ReplicaStates
            .Where(r => !r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase) && !r.Name.Equals(health.PrimaryReplica, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Any(r => r.AvailabilityMode == "SYNCHRONOUS_COMMIT" && r.IsFailoverReady && !Requested(ag, r.Name)))
            return verdict;
        var candidate = others
            .Where(r => r.AvailabilityMode == "ASYNCHRONOUS_COMMIT" && r.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
                        && r.SynchronizationHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)
                        && !Requested(ag, r.Name) && !Held(ag, r.Name))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
            return verdict;
        maintenance.Reinforced ??= [];
        if (!maintenance.Reinforced.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
            maintenance.Reinforced.Add(candidate.Name);
        return verdict with
        {
            Decision = VerdictDecision.Delay,
            Reason = $"making {candidate.Name} synchronous before {pod} leaves, so that a synchronized copy remains",
        };
    }

    /// <summary>The reinforced replicas still asynchronous, to make synchronous now.</summary>
    public IReadOnlyList<string> ToReinforce(SqlAvailabilityGroup ag, AgHealthReport health)
        => (ag.Status?.Maintenance?.Reinforced ?? [])
            .Where(pod => health.ReplicaStates.Any(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase) && r.AvailabilityMode == "ASYNCHRONOUS_COMMIT"))
            .ToList();

    /// <summary>Once no eviction stands and every held replica is back, a reinforced replica
    /// goes back to asynchronous commit (Lower true) — but only when another synchronous secondary is
    /// synchronized, so that the copy it kept is never the last one; one that is asynchronous already
    /// is just released.</summary>
    public IReadOnlyList<(string Pod, bool Lower)> ToUnreinforce(SqlAvailabilityGroup ag, AgHealthReport health)
    {
        var maintenance = ag.Status?.Maintenance;
        if (maintenance?.Reinforced is not { Count: > 0 } reinforced || maintenance.AsyncHold is { Count: > 0 }
            || health.ReplicaStates.Any(r => Requested(ag, r.Name)))
            return [];
        var result = new List<(string, bool)>();
        foreach (var pod in reinforced)
        {
            var state = health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase));
            if (state is null)
                continue;
            if (state.AvailabilityMode != "SYNCHRONOUS_COMMIT")
                result.Add((pod, false));
            else if (health.ReplicaStates.Any(r => !r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase)
                                                   && !r.Name.Equals(health.PrimaryReplica, StringComparison.OrdinalIgnoreCase)
                                                   && r.AvailabilityMode == "SYNCHRONOUS_COMMIT" && r.IsFailoverReady))
                result.Add((pod, true));
        }
        return result;
    }

    /// <summary>The replica is asynchronous again: no longer reinforced.</summary>
    public static void EndReinforcement(SqlAvailabilityGroup ag, string pod)
    {
        if (ag.Status?.Maintenance?.Reinforced is { } reinforced)
        {
            reinforced.RemoveAll(p => p.Equals(pod, StringComparison.OrdinalIgnoreCase));
            if (reinforced.Count == 0)
                ag.Status.Maintenance.Reinforced = null;
        }
    }

    /// <summary>The held replicas to make asynchronous now (still synchronous, request standing).</summary>
    public IReadOnlyList<string> ToLower(SqlAvailabilityGroup ag, AgHealthReport health)
        => (ag.Status?.Maintenance?.AsyncHold ?? []).Where(pod => Synchronous(health, pod) && Requested(ag, pod)).ToList();

    /// <summary>The held replicas whose maintenance is over — no request left (the pod came
    /// back, or the drain gave up) and connected again, or never lowered: to make synchronous again
    /// when they are not, and to release.</summary>
    public IReadOnlyList<(string Pod, bool Raise)> ToRestore(SqlAvailabilityGroup ag, AgHealthReport health)
        => (ag.Status?.Maintenance?.AsyncHold ?? [])
            .Where(pod => !Requested(ag, pod))
            .Select(pod => (pod, State: health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.State is not null && (x.State.AvailabilityMode == "SYNCHRONOUS_COMMIT" || x.State.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)))
            .Select(x => (x.pod, Raise: x.State!.AvailabilityMode != "SYNCHRONOUS_COMMIT"))
            .ToList();

    /// <summary>The replica is synchronous again (or was never lowered): no longer held.</summary>
    public static void Release(SqlAvailabilityGroup ag, string pod)
    {
        if (ag.Status?.Maintenance?.AsyncHold is { } hold)
        {
            hold.RemoveAll(p => p.Equals(pod, StringComparison.OrdinalIgnoreCase));
            if (hold.Count == 0)
                ag.Status.Maintenance.AsyncHold = null;
        }
    }

    private bool Requested(SqlAvailabilityGroup ag, string pod)
    {
        var ns = ag.Metadata.NamespaceProperty;
        return signals.PodsOf("SqlAvailabilityGroup", ns is null ? ag.Metadata.Name : $"{ns}/{ag.Metadata.Name}")
            .FirstOrDefault(p => p.Name.Equals(pod, StringComparison.OrdinalIgnoreCase))?.EvictionRecord is { } record
               && !EvictionRequests.IsAbandoned(record, timeProvider.GetUtcNow());
    }

    /// <summary>The replicas as the verdicts see them, from the health pass.</summary>
    internal IReadOnlyList<ReplicaView> ReplicasOf(SqlAvailabilityGroup ag, AgHealthReport health)
    {
        var ns = ag.Metadata.NamespaceProperty;
        var name = ag.Metadata.Name;
        var facts = signals.PodsOf("SqlAvailabilityGroup", ns is null ? name : $"{ns}/{name}")
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var primaryDatabases = health.PrimaryReplica is { } primary
            ? health.DatabaseStates.Where(d => d.ReplicaName.Equals(primary, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.DatabaseName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var replicas = new List<ReplicaView>();
        for (var i = 0; i < ag.Spec.Replicas.Count; i++)
        {
            var pod = $"{name}-{i}";
            var state = health.ReplicaStates.FirstOrDefault(r => r.Name.Equals(pod, StringComparison.OrdinalIgnoreCase));
            var fact = facts.GetValueOrDefault(pod);
            var isPrimary = state?.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) == true;
            var ready = state is not null && fact is { DeletionTimestamp: null }
                        && health.ReachableReplicas.Contains(pod, StringComparer.OrdinalIgnoreCase)
                        && (isPrimary || state.ConnectionState.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase));
            var databases = health.DatabaseStates.Where(d => d.ReplicaName.Equals(pod, StringComparison.OrdinalIgnoreCase)).ToList();
            var held = databases.Select(d => d.DatabaseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            replicas.Add(new ReplicaView(
                pod,
                fact?.Node,
                isPrimary,
                ready,
                Synchronized: isPrimary || state?.IsFailoverReady == true,
                // Made asynchronous for the maintenance, it is still a synchronous replica that
                // the next departures wait for.
                SynchronousCommit: isPrimary || state?.AvailabilityMode == "SYNCHRONOUS_COMMIT" || Held(ag, pod),
                Seeding: !isPrimary && primaryDatabases.Any(d => !held.Contains(d)),
                RedoSeconds: isPrimary || databases.Count == 0 ? 0 : databases.Max(RedoSecondsOf)));
        }
        return replicas;
    }

    /// <summary>The seconds of redo a database still has on a replica, at its redo rate, counted at
    /// 1 MB/s at least (an idle replica reports a rate of 0).</summary>
    internal static double RedoSecondsOf(MsSqlOperator.Services.SqlServer.AgDatabaseStateInfo database)
        => database.RedoQueueSize <= 0 ? 0 : database.RedoQueueSize / (double)Math.Max(database.RedoRate, 1024);

    /// <summary>The request and its verdict in <c>status.maintenance</c>.</summary>
    private static void Report(MaintenanceStatus? maintenance, string pod, EvictionRecord record, Verdict verdict)
    {
        if (maintenance?.Pods.FirstOrDefault(p => p.Pod == pod) is not { } status)
            return;
        status.RequestedAt = record.RequestedAt.UtcDateTime;
        status.RequestedBy = record.RequestedBy;
        status.Deadline = verdict.Deadline.UtcDateTime;
        status.Verdict = verdict.Decision.ToString();
        status.Reason = verdict.Reason;
    }

    private async Task PublishVerdictAsync(SqlAvailabilityGroup ag, string pod, Verdict verdict, SwitchMode mode, DateTimeOffset now, CancellationToken ct)
    {
        var ns = ag.Metadata.NamespaceProperty!;
        var key = $"{ns}/{pod}";
        if (!_reported.TryGetValue(key, out var reported) || reported != (verdict.Decision, verdict.Reason))
        {
            _reported[key] = (verdict.Decision, verdict.Reason);
            var shadow = mode == SwitchMode.Shadow ? "Shadow, the eviction is not held: " : "";
            logger.LogInformation("AG {Namespace}/{Name}: eviction of {Pod}: {Decision}, {Reason} (mode {Mode})",
                ns, ag.Metadata.Name, pod, verdict.Decision, verdict.Reason, mode);
            await eventPublisher.PublishAsync(ag, verdict.Decision == VerdictDecision.Delay ? "EvictionDelayed" : "EvictionAllowed",
                $"{shadow}{pod}: {verdict.Reason}.", ct: ct);
        }

        if (mode != SwitchMode.Enforce)
        {
            _published.TryRemove(key, out _);
            return;
        }
        if (_published.TryGetValue(key, out var written) && written.Decision == verdict.Decision && written.Reason == verdict.Reason
            && written.ValidUntil - now > EvictionRequests.VerdictValidity / 2)
            return;
        var next = new PublishedVerdict(verdict.Decision, verdict.Reason, verdict.Deadline, now + EvictionRequests.VerdictValidity);
        try
        {
            await client.PatchPodAnnotationsAsync(pod, ns,
                new Dictionary<string, string?> { [MaintenanceSignals.VerdictAnnotation] = EvictionAnnotations.Serialize(next) }, ct);
            _published[key] = next;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The previous verdict expires on its own; the webhook then delays until the deadline.
            logger.LogWarning(e, "AG {Namespace}/{Name}: could not publish the eviction verdict of {Pod}", ns, ag.Metadata.Name, pod);
        }
    }

    /// <summary>The manual release applies to the requests it found: removed once none is left.</summary>
    private async Task ClearReleaseAsync(SqlAvailabilityGroup ag, CancellationToken ct)
    {
        if (ag.Metadata.Annotations?.ContainsKey(MaintenanceObserver.ReleaseAnnotation) != true)
            return;
        try
        {
            await client.PatchMetadataAsync(ag, new Dictionary<string, string?> { [MaintenanceObserver.ReleaseAnnotation] = null }, ct);
            await eventPublisher.PublishAsync(ag, "MaintenanceReleaseConsumed",
                "No eviction request is left: the maintenance-release annotation is removed.", ct: ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "AG {Namespace}/{Name}: could not remove the maintenance-release annotation",
                ag.Metadata.NamespaceProperty, ag.Metadata.Name);
        }
    }
}
