using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.Actuation;

/// <summary>
/// The one way to a disruptive or destructive action. It decides (policy: mode, risk,
/// approvals, budget), checks the preconditions again right before acting, records the decision in
/// the plan of the resource, counts it, says it in an event when the action waits, and consumes the
/// approval it used. An action it does not run throws <see cref="ActionWithheldException"/>.
/// </summary>
public sealed class ActuationGateway(ActuationPolicy policy, PlanStore plans, IKubernetesClient? client = null,
    IEventPublisher? events = null, Metrics.OperatorMetrics? metrics = null, TimeProvider? clock = null,
    ILogger<ActuationGateway>? logger = null, SqlServer.IEpochStamper? stamper = null, Control.IOperatorEpoch? epoch = null,
    Func<IKubernetesObject<V1ObjectMeta>, Core.Safety.SafetyMode?>? epochMode = null, string leader = "mssql-operator")
{
    /// <summary>
    /// The SQL effect of a topology action, under the applock and the epoch stamp of its AG
    /// (<paramref name="scope"/>). A stamp above the operator's epoch raises it — and, in
    /// Enforce, refuses the command: a leader that was paused cannot undo what a newer one did.
    /// </summary>
    public Func<CancellationToken, Task> Stamped(IKubernetesObject<V1ObjectMeta> owner, Microsoft.Data.SqlClient.SqlConnection connection,
        string scope, Func<CancellationToken, Task> effect)
    {
        var mode = epochMode?.Invoke(owner);
        if (stamper is null || epoch is null || mode is null)
            return effect;
        return async ct =>
        {
            try
            {
                var stamped = await stamper.RunAsync(connection, scope, epoch.Current, leader, mode == Core.Safety.SafetyMode.Enforce, effect, ct);
                if (stamped > epoch.Current)
                {
                    logger?.LogWarning("Stale epoch {Epoch} on {Scope} (stamped {Stamped}) — Shadow: the command ran; raising the epoch",
                        epoch.Current, scope, stamped);
                    await epoch.RaiseAboveAsync(stamped, ct);
                }
            }
            catch (SqlServer.StaleEpochException ex)
            {
                await epoch.RaiseAboveAsync(ex.Stamped, ct);
                throw;
            }
        };
    }

    public const string ApproveAnnotation = "mssql-operator.evariops.com/approve";

    /// <summary>The gateway of a component built without one (unit tests): the default policy, in
    /// Apply mode, with its own plan store.</summary>
    public static ActuationGateway Default { get; } =
        new(new ActuationPolicy(new AutomationOptions(), TimeProvider.System), new PlanStore());

    public PlanStore Plans => plans;

    /// <summary>Whether the gateway acts for <paramref name="owner"/> (Apply mode). A controller
    /// leaves a human's request — an annotation — in place when it would not act on it.</summary>
    public bool Acts(IKubernetesObject<V1ObjectMeta> owner) => policy.EffectiveMode(AutomationOf(owner)) == AutomationMode.Apply;

    public async Task RunAsync(IKubernetesObject<V1ObjectMeta> owner, ActionRequest request, Func<CancellationToken, Task> effect,
        CancellationToken ct, ActuationIntent intent = ActuationIntent.Operator, Func<CancellationToken, Task<string?>>? precondition = null)
    {
        var key = PlanStore.Key(owner.Kind ?? owner.GetType().Name, owner.Metadata.NamespaceProperty, owner.Metadata.Name);
        var approvals = Approvals(owner);
        var decision = policy.Decide(request, key, AutomationOf(owner), approvals, intent);
        if (decision.Proceeds && precondition is not null && await precondition(ct) is { } why)
            decision = new(DecisionKind.Denied, $"precondition no longer holds: {why}");
        metrics?.ActuationDecision(request.Risk.ToString(), decision.Kind.ToString());
        Record(key, request, decision, executed: false);

        if (!decision.Proceeds)
        {
            await AnnounceWithheldAsync(owner, request, decision, ct);
            throw new ActionWithheldException(request, decision);
        }

        await effect(ct);
        policy.Spent(request, key, decision);
        Record(key, request, decision, executed: true);
        logger?.LogInformation("{Id} run ({Decision}: {Reason})", request.Id, decision.Kind, decision.Reason);

        if (decision.Kind == DecisionKind.Approved && approvals.Contains(request.Id) && client is not null)
            await ConsumeApprovalAsync(owner, request.Id, approvals, ct);
    }

    /// <summary>
    /// Whether <paramref name="request"/> would run now, decided as <see cref="RunAsync"/>
    /// would decide it, without running it, spending a budget or consuming an approval. One that
    /// would not run is planned and announced, as <see cref="RunAsync"/> does. For a sequence whose
    /// first steps cannot be undone by the gateway: a planned failover demoted the primary, then
    /// its promotion was withheld (Plan mode) — the AG stayed without a primary for 12 minutes.
    /// </summary>
    public async Task<bool> WouldRunAsync(IKubernetesObject<V1ObjectMeta> owner, ActionRequest request, CancellationToken ct,
        ActuationIntent intent = ActuationIntent.Operator)
    {
        var key = PlanStore.Key(owner.Kind ?? owner.GetType().Name, owner.Metadata.NamespaceProperty, owner.Metadata.Name);
        var decision = policy.Decide(request, key, AutomationOf(owner), Approvals(owner), intent);
        if (decision.Proceeds)
            return true;
        metrics?.ActuationDecision(request.Risk.ToString(), decision.Kind.ToString());
        Record(key, request, decision, executed: false);
        await AnnounceWithheldAsync(owner, request, decision, ct);
        return false;
    }

    private async Task AnnounceWithheldAsync(IKubernetesObject<V1ObjectMeta> owner, ActionRequest request, PolicyDecision decision,
        CancellationToken ct)
    {
        var (reason, type) = decision.Kind switch
        {
            DecisionKind.RequiresApproval => ("ActionRequiresApproval", "Warning"),
            DecisionKind.Denied => ("ActionDenied", "Warning"),
            _ => ("ActionPlanned", "Normal"),
        };
        if (events is not null)
            await events.PublishAsync(owner, reason, $"{request.Description} [{request.Id}]: {decision.Reason}", type, ct);
    }

    private void Record(string key, ActionRequest request, PolicyDecision decision, bool executed)
        => plans.Record(key, new PlannedAction(request.Id, request.Type.ToString(), request.Risk.ToString(), request.Target,
            request.Description, decision.Kind.ToString(), decision.Reason, executed, (clock ?? TimeProvider.System).GetUtcNow()));

    private static List<string> Approvals(IKubernetesObject<V1ObjectMeta> owner)
        => owner.Metadata.Annotations?.TryGetValue(ApproveAnnotation, out var value) == true
            ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

    private async Task ConsumeApprovalAsync(IKubernetesObject<V1ObjectMeta> owner, string id, List<string> approvals, CancellationToken ct)
    {
        var rest = approvals.Where(a => a != id).ToList();
        try
        {
            await PatchAsync((dynamic)owner, rest, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Could not consume the approval {Id}; it stays on the resource", id);
        }
    }

    private Task PatchAsync<T>(T owner, List<string> rest, CancellationToken ct) where T : class, IKubernetesObject<V1ObjectMeta>
        => client!.PatchMetadataAsync(owner, new Dictionary<string, string?> { [ApproveAnnotation] = rest.Count == 0 ? null : string.Join(',', rest) }, ct);

    /// <summary>spec.automation of an operator resource; null for anything else (a pod), which then
    /// runs under the operator's ceiling.</summary>
    internal static Entities.Common.AutomationSpec? AutomationOf(object owner) => owner switch
    {
        Entities.V1Alpha1.SqlAvailabilityGroup r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlAvailabilityGroupDatabase r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlInstance r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlUser r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlAgentJob r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlStoredProcedure r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlTdeCertificate r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlConfiguration r => r.Spec?.Automation,
        Entities.V1Alpha1.SqlDatabase r => r.Spec?.Automation,
        _ => null,
    };

    /// <summary>The epoch part of an action id: the generation of the resource.</summary>
    public static string Epoch(IKubernetesObject<V1ObjectMeta> owner) => (owner.Metadata.Generation ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
