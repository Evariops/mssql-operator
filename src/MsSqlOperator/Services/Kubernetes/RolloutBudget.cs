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
