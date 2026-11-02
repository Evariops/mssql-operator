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
        // disruption taint): the primary moves now, once, while nothing presses yet.
        if (health?.PrimaryReplica is { } primary && plannedFailover
            && signalsByPod.TryGetValue(primary, out var primarySignals) && primarySignals.Any(s => s.Scope == SignalScope.Node)
            && EvictionVerdicts.FailoverTargetFor(primary, replicas, signalsByPod, preferred) is { Now: true, Target: { } early })
            await RequestFailoverAsync(ag, primary, early, mode, $"its node announces a stop ({string.Join(", ", primarySignals.Select(s => s.Kind).Distinct())})", ct);

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
                SynchronousCommit: isPrimary || state?.AvailabilityMode == "SYNCHRONOUS_COMMIT",
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
