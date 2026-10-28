using k8s;
using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Registration;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// The two holds on a StatefulSet change that restarts SQL Server: an in-place cumulative
/// update that only comes from a moved operator default waits for approval (the running image is
/// kept), and a pod-template change waits while the fleet rollout budget is spent.
/// </summary>
public static class RolloutGate
{
    /// <summary>Adjusts <paramref name="desired"/> (image, template-hash annotation) and returns
    /// whether it may be applied now.</summary>
    public static async Task<bool> ApplyAsync(V1StatefulSet desired, V1StatefulSet? live, SqlImageSelection selection,
        IDictionary<string, string>? annotations, bool onDelete, List<ConditionStatus> conditions,
        IKubernetesObject<V1ObjectMeta> owner, IRolloutBudget budget, IEventPublisher eventPublisher,
        OperatorOptions options, CancellationToken ct)
    {
        var container = desired.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "mssql");
        var liveImage = live?.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "mssql")?.Image;
        var (image, held) = SqlServerImages.DecideEngineImage(selection, liveImage, annotations,
            automatic: options.EngineUpgradePolicy.Equals("Automatic", StringComparison.OrdinalIgnoreCase));
        if (container is not null)
            container.Image = image;
        ConditionOps.Set(conditions, "EngineUpgradePending", held is null ? "False" : "True",
            held is null ? "NoPendingUpgrade" : "AwaitingApproval",
            held ?? "The StatefulSet runs the build the spec resolves to");
        if (held is not null)
            await eventPublisher.PublishAsync(owner, "EngineUpgradePending", held, "Warning", ct);

        var hash = RolloutBudget.TemplateHash(desired);
        desired.Metadata ??= new V1ObjectMeta();
        (desired.Metadata.Annotations ??= new Dictionary<string, string>())[RolloutBudget.TemplateHashAnnotation] = hash;
        string? liveHash = null;
        live?.Metadata.Annotations?.TryGetValue(RolloutBudget.TemplateHashAnnotation, out liveHash);
        var restarts = live is not null && !onDelete && liveHash != hash
                       && (liveHash is not null || await budget.ChangesTemplateAsync(desired, live, ct));
        if (restarts)
        {
            var (allowed, heldBy, stuck) = await budget.MayStartAsync(desired.Metadata.NamespaceProperty, desired.Metadata.Name, ct);
            if (stuck.Count > 0)
                // A StatefulSet that never came back ready no longer holds the fleet.
                await eventPublisher.PublishAsync(owner, "RolloutStuck",
                    $"Not ready for more than {RolloutBudget.StuckAfter.TotalMinutes:0} min, no longer counted in the fleet rollout budget: " +
                    string.Join(", ", stuck.Take(3)) + (stuck.Count > 3 ? ", …" : ""), "Warning", ct);
            if (!allowed)
            {
                var message = $"A change of the pod template (restart of SQL Server) waits for the rollouts in progress " +
                              $"({heldBy}) — operator.maxConcurrentRestarts={Math.Max(1, options.MaxConcurrentRestarts)}.";
                ConditionOps.Set(conditions, "RestartDeferred", "True", "FleetRolloutBudget", message);
                await eventPublisher.PublishAsync(owner, "RestartDeferred", message, ct: ct);
                return false;
            }
        }

        ConditionOps.Set(conditions, "RestartDeferred", "False", "NotDeferred", "No restart is waiting for the fleet rollout budget");
        return true;
    }
}
