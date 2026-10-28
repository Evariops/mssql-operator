using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Fleet-wide cap on pod-template rollouts. A template change (operator upgrade, a shared
/// SqlConfiguration edit) restarts SQL Server; applying it to every instance at once restarted the
/// whole fleet together. The state is read from the cluster — StatefulSets of the operator that are
/// still rolling or not fully ready — so it survives an operator restart, and an instance stuck after
/// its change (a bad template) holds the rest of the fleet back instead of being joined by it.
/// </summary>
public interface IRolloutBudget
{
    /// <summary>Whether <paramref name="name"/> may start a rollout now — the slot is then reserved —
    /// and if not, which StatefulSets hold the budget.</summary>
    Task<RolloutDecision> MayStartAsync(string ns, string name, CancellationToken ct);

    /// <summary>Whether applying <paramref name="desired"/> changes the pod template of
    /// <paramref name="live"/> — for a StatefulSet that carries no template hash (created by an
    /// earlier version). When it cannot be told, the answer is yes.</summary>
    Task<bool> ChangesTemplateAsync(V1StatefulSet desired, V1StatefulSet live, CancellationToken ct);
}

/// <param name="Allowed">The rollout may start: a slot is reserved for it.</param>
/// <param name="HeldBy">The StatefulSets holding the budget, when not allowed.</param>
/// <param name="Stuck">StatefulSets busy for longer than <see cref="RolloutBudget.StuckAfter"/>: they no
/// longer hold the budget.</param>
public sealed record RolloutDecision(bool Allowed, string? HeldBy, IReadOnlyList<string> Stuck);

/// <summary>
/// The budget is reserved in the <c>mssql-operator-rollout-slots</c> Lease, by
/// compare-and-swap on its annotation. Two passes that both saw room in the cluster used to start
/// two rollouts; the second write now fails and reads again. The annotation records since when each
/// StatefulSet holds the budget: one busy for longer than <see cref="StuckAfter"/> (a bad template, a
/// replica on a lost node) no longer holds the fleet back — it is reported instead.
/// </summary>
public sealed class RolloutBudget(IKubernetesClient kubernetesClient, OperatorOptions options, TimeProvider? timeProvider = null,
    ILogger<RolloutBudget>? logger = null) : IRolloutBudget
{
    public const string TemplateHashAnnotation = "mssql-operator.evariops.com/pod-template-hash";
    public const string SlotsLeaseName = "mssql-operator-rollout-slots";
    public const string SlotsAnnotation = "mssql-operator.evariops.com/rollout-slots";

    /// <summary>A StatefulSet busy for longer no longer holds the budget.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>How long a reservation holds while its StatefulSet has not started rolling yet.</summary>
    public static readonly TimeSpan ReservationGrace = TimeSpan.FromMinutes(2);

    private const int Attempts = 3;

    public async Task<RolloutDecision> MayStartAsync(string ns, string name, CancellationToken ct)
    {
        var self = $"{ns}/{name}";
        var all = await kubernetesClient.ListAsync<V1StatefulSet>(null, OperatorLabels.ManagedBySelector, ct);
        var busy = all.Where(IsBusy).Select(s => $"{s.Metadata.NamespaceProperty}/{s.Metadata.Name}").ToHashSet();
        var max = Math.Max(1, options.MaxConcurrentRestarts);
        var leaseNamespace = options.ResolveNamespace();

        for (var attempt = 1; ; attempt++)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            try
            {
                var lease = await kubernetesClient.GetAsync<V1Lease>(SlotsLeaseName, leaseNamespace, ct);
                var slots = Read(lease);
                var (next, holders, stuck) = Account(slots, busy, self, now);
                var allowed = holders.Count < max;
                if (allowed)
                    next[self] = now;
                if (!SameSlots(slots, next))
                    await WriteAsync(lease, leaseNamespace, next, ct);
                return allowed
                    ? new RolloutDecision(true, null, stuck)
                    : new RolloutDecision(false, string.Join(", ", holders.Take(3)) + (holders.Count > 3 ? ", …" : ""), stuck);
            }
            catch (Exception ex) when (ex is k8s.Autorest.HttpOperationException or HttpRequestException && !ct.IsCancellationRequested)
            {
                // Another pass reserved meanwhile (the resourceVersion test failed), or the Lease is out
                // of reach: read again, then wait — never start without a reservation.
                if (attempt >= Attempts)
                {
                    logger?.LogWarning(ex, "Rollout slot of {StatefulSet} could not be reserved; the rollout waits", self);
                    return new RolloutDecision(false, $"the {SlotsLeaseName} Lease (reservation not written)", []);
                }
            }
        }
    }

    /// <summary>The slots to keep, the StatefulSets holding the budget, those stuck. A busy
    /// StatefulSet keeps the time it was first seen busy (or reserved); a reservation whose StatefulSet
    /// is not busy holds for <see cref="ReservationGrace"/> — the rollout has not shown yet.</summary>
    internal static (Dictionary<string, DateTimeOffset> Next, List<string> Holders, List<string> Stuck) Account(
        IReadOnlyDictionary<string, DateTimeOffset> slots, IReadOnlySet<string> busy, string self, DateTimeOffset now)
    {
        var next = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var key in busy)
            next[key] = slots.TryGetValue(key, out var since) && since <= now ? since : now;
        foreach (var (key, since) in slots)
            if (!busy.Contains(key) && now - since < ReservationGrace)
                next[key] = since;
        var holders = next.Where(e => e.Key != self && now - e.Value < StuckAfter).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
        var stuck = next.Where(e => e.Key != self && now - e.Value >= StuckAfter).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
        return (next, holders, stuck);
    }

    private static Dictionary<string, DateTimeOffset> Read(V1Lease? lease)
    {
        if (lease?.Metadata?.Annotations?.TryGetValue(SlotsAnnotation, out var json) != true || string.IsNullOrEmpty(json))
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json)
                   ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            // Written by hand or by another version: starting over only forgets since when.
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    private static bool SameSlots(IReadOnlyDictionary<string, DateTimeOffset> a, IReadOnlyDictionary<string, DateTimeOffset> b)
        => a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var v) && v == e.Value);

    private async Task WriteAsync(V1Lease? lease, string leaseNamespace, Dictionary<string, DateTimeOffset> slots, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SortedDictionary<string, DateTimeOffset>(slots, StringComparer.Ordinal));
        if (lease is null)
            // Two creations race on the name: the second gets a 409 and reads again.
            await kubernetesClient.CreateAsync(new V1Lease
            {
                Metadata = new V1ObjectMeta
                {
                    Name = SlotsLeaseName,
                    NamespaceProperty = leaseNamespace,
                    Annotations = new Dictionary<string, string> { [SlotsAnnotation] = json },
                },
                Spec = new V1LeaseSpec(),
            }, ct);
        else
            await kubernetesClient.PatchMetadataAsync(lease, new Dictionary<string, string?> { [SlotsAnnotation] = json }, ct);
    }

    public async Task<bool> ChangesTemplateAsync(V1StatefulSet desired, V1StatefulSet live, CancellationToken ct)
    {
        // Both templates come back from the API server (defaulted, merged with the fields of other
        // managers), so an unchanged template hashes the same. Without the dry run every StatefulSet
        // of the previous version counted as a restart, and the whole fleet waited on the budget.
        try
        {
            var merged = await kubernetesClient.DryRunApplyAsync(desired, ct);
            return TemplateHash(merged) != TemplateHash(live);
        }
        catch (Exception e) when (e is k8s.Autorest.HttpOperationException or HttpRequestException)
        {
            return true;
        }
    }

    /// <summary>Rolling (revisions differ) or not all replicas ready.</summary>
    internal static bool IsBusy(V1StatefulSet sts)
        => sts.Status is { } status
           && ((status.UpdateRevision is not null && status.CurrentRevision != status.UpdateRevision)
               || (status.ReadyReplicas ?? 0) < (sts.Spec?.Replicas ?? 1));

    /// <summary>Hash of the pod template, stamped on the StatefulSet so a pending change is
    /// recognisable without a dry run.</summary>
    public static string TemplateHash(V1StatefulSet sts)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sts.Spec?.Template is { } template ? k8s.KubernetesJson.Serialize(template) : string.Empty)))[..16];
}
