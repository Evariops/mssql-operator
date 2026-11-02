using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Services.Maintenance;

/// <summary>
/// Turns the signals of <see cref="MaintenanceSignals"/> into the status of a resource:
/// <c>status.maintenance</c>, the <c>NodeMaintenance</c> condition, a <c>MaintenanceSignal</c> event
/// per new signal, and the <c>mssqloperator.maintenance.in_progress</c> gauge. It only sets the
/// in-memory status; the reconcile writes it with the rest, and an unchanged status is not written.
/// Without any signal, a resource that never had one keeps no maintenance field and no condition:
/// nothing is written in steady state.
/// </summary>
public sealed class MaintenanceObserver(
    MaintenanceSignals signals,
    OperatorOptions options,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics,
    TimeProvider timeProvider,
    ILogger<MaintenanceObserver> logger)
{
    public const string Condition = "NodeMaintenance";

    /// <summary>Per resource: <c>Off</c>, <c>Shadow</c> or <c>Enforce</c>, capped by the operator's
    /// <c>Operator:NodeMaintenance</c>.</summary>
    public const string ModeAnnotation = "mssql-operator.evariops.com/node-maintenance";

    /// <summary>On the resource, any value releases at once the evictions its pods wait for.
    /// The leader removes it when no eviction request is left.</summary>
    public const string ReleaseAnnotation = "mssql-operator.evariops.com/maintenance-release";

    /// <summary>The mode in force: the annotation when it is a lower one, else the operator's.</summary>
    public SwitchMode ModeOf(IKubernetesObject<V1ObjectMeta> resource)
    {
        var ceiling = options.NodeMaintenance;
        return resource.Metadata.Annotations?.TryGetValue(ModeAnnotation, out var value) == true
               && Enum.TryParse<SwitchMode>(value, ignoreCase: true, out var own) && own < ceiling
            ? own
            : ceiling;
    }

    /// <summary>Updates <paramref name="current"/> in place (or returns a new one) from the signals of
    /// the resource's pods. Returns the maintenance status to keep, null when none.</summary>
    public async Task<MaintenanceStatus?> ObserveAsync(IKubernetesObject<V1ObjectMeta> resource, MaintenanceStatus? current,
        List<ConditionStatus> conditions, CancellationToken ct)
    {
        var mode = ModeOf(resource);
        var kind = resource.Kind ?? resource.GetType().Name;
        var ns = resource.Metadata.NamespaceProperty;
        var name = resource.Metadata.Name;
        if (mode == SwitchMode.Off)
        {
            metrics.SetMaintenanceInProgress(kind, ns, name, false);
            return current;
        }

        // Before the first list of pods and nodes, an absence of signal proves nothing.
        if (!signals.IsSynced)
            return current;

        var observed = signals.For(kind, ns is null ? name : $"{ns}/{name}");
        metrics.SetMaintenanceInProgress(kind, ns, name, observed.Count > 0);

        if (observed.Count == 0)
        {
            if (current is null)
                return null;
            // A replica still asynchronous for the maintenance keeps the status until restored.
            if (current.AsyncHold is { Count: > 0 } || current.Reinforced is { Count: > 0 })
                return new MaintenanceStatus
                {
                    Mode = mode.ToString(),
                    AsyncHold = current.AsyncHold,
                    Reinforced = current.Reinforced,
                    FailoverFrom = current.FailoverFrom,
                };
            ConditionOps.Set(conditions, Condition, "False", "NoSignal", "No pod of the resource is about to be stopped by a node maintenance.",
                now: timeProvider.GetUtcNow().UtcDateTime);
            await eventPublisher.PublishAsync(resource, "MaintenanceEnded",
                $"No more maintenance signal on {string.Join(", ", current.Pods.Select(p => p.Pod))}", ct: ct);
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // The maintenance failover lives as long as the maintenance.
        var next = new MaintenanceStatus
        {
            Mode = mode.ToString(),
            FailoverTarget = current?.FailoverTarget,
            FailoverFrom = current?.FailoverFrom,
            AsyncHold = current?.AsyncHold,
            Reinforced = current?.Reinforced,
        };
        foreach (var pod in observed)
        {
            var previous = current?.Pods.FirstOrDefault(p => p.Pod == pod.Pod);
            var kinds = pod.Signals.Select(s => s.Kind.ToString()).Distinct().ToList();
            next.Pods.Add(new MaintenancePodStatus { Pod = pod.Pod, Node = pod.Node, Signals = kinds, Since = previous?.Since ?? now });

            var added = pod.Signals.Where(s => previous?.Signals.Contains(s.Kind.ToString()) != true).ToList();
            if (added.Count > 0)
            {
                var detail = string.Join("; ", added.Select(s => s.Detail));
                logger.LogInformation("{Kind} {Namespace}/{Name}: maintenance signal on {Pod} ({Detail}), mode {Mode}",
                    kind, ns, name, pod.Pod, detail, mode);
                await eventPublisher.PublishAsync(resource, "MaintenanceSignal",
                    $"{pod.Pod}{(pod.Node is null ? "" : $" on {pod.Node}")}: {detail}." +
                    (mode == SwitchMode.Shadow ? " Shadow: observed only (Operator:NodeMaintenance)." : ""), ct: ct);
            }
        }

        ConditionOps.Set(conditions, Condition, "True", mode == SwitchMode.Shadow ? "SignalsObserved" : "SignalsDetected",
            string.Join("; ", next.Pods.Select(p => $"{p.Pod}: {string.Join(", ", p.Signals)}")), now: now);
        return next;
    }
}
