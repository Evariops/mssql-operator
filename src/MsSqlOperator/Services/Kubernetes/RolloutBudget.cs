using k8s.Models;
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
    /// <summary>Whether <paramref name="name"/> may start a rollout now, and if not, which
    /// StatefulSets hold the budget.</summary>
    Task<(bool Allowed, string? HeldBy)> MayStartAsync(string ns, string name, CancellationToken ct);

    /// <summary>Whether applying <paramref name="desired"/> changes the pod template of
    /// <paramref name="live"/> — for a StatefulSet that carries no template hash (created by an
    /// earlier version). When it cannot be told, the answer is yes.</summary>
    Task<bool> ChangesTemplateAsync(V1StatefulSet desired, V1StatefulSet live, CancellationToken ct);
}

public sealed class RolloutBudget(IKubernetesClient kubernetesClient, OperatorOptions options) : IRolloutBudget
{
    public const string TemplateHashAnnotation = "mssql-operator.evariops.com/pod-template-hash";

    public async Task<(bool Allowed, string? HeldBy)> MayStartAsync(string ns, string name, CancellationToken ct)
    {
        var all = await kubernetesClient.ListAsync<V1StatefulSet>(null, OperatorLabels.ManagedBySelector, ct);
        var busy = all
            .Where(s => !(s.Metadata.NamespaceProperty == ns && s.Metadata.Name == name) && IsBusy(s))
            .Select(s => $"{s.Metadata.NamespaceProperty}/{s.Metadata.Name}")
            .ToList();
        return busy.Count < Math.Max(1, options.MaxConcurrentRestarts)
            ? (true, null)
            : (false, string.Join(", ", busy.Take(3)) + (busy.Count > 3 ? ", …" : ""));
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
