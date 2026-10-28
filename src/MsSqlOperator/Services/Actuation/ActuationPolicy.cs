using System.Collections.Concurrent;

namespace MsSqlOperator.Services.Actuation;

/// <summary>Observe, Plan or Apply — ordered: the lower of the resource's mode and the operator's
/// ceiling wins.</summary>
public enum AutomationMode { Observe, Plan, Apply }

public sealed class AutomationOptions
{
    public const string SectionName = "Operator:Automation";

    /// <summary>The ceiling of every resource's mode (Operator:Automation:MaxMode).</summary>
    public AutomationMode MaxMode { get; set; } = AutomationMode.Apply;

    /// <summary>Pre-authorized destructive actions the whole operator may run in an hour.</summary>
    public int MaxDestructivePerHour { get; set; } = 3;
}

/// <summary>
/// Decides an action. A Destructive one is never automatic, unless the resource is being
/// deleted (the cleanup of its own finalizer), a human approved it (the approve annotation, or the
/// controller's own approval annotation), or the resource pre-authorizes its type within a budget —
/// one an hour per resource, and a global ceiling. A Disruptive one runs in Apply mode.
/// </summary>
public sealed class ActuationPolicy(AutomationOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDestructive = new();
    private readonly ConcurrentQueue<DateTimeOffset> _destructiveRuns = new();

    public static AutomationMode ModeOf(Entities.Common.AutomationSpec? spec)
        => Enum.TryParse<AutomationMode>(spec?.Mode, ignoreCase: true, out var mode) ? mode : AutomationMode.Apply;

    /// <summary>The mode the resource runs under: its own, capped by the operator's ceiling.</summary>
    public AutomationMode EffectiveMode(Entities.Common.AutomationSpec? automation)
        => (AutomationMode)Math.Min((int)ModeOf(automation), (int)options.MaxMode);

    public PolicyDecision Decide(ActionRequest request, string owner, Entities.Common.AutomationSpec? automation,
        IReadOnlyCollection<string> approvals, ActuationIntent intent)
    {
        if (ActionCatalog.AlwaysDenied(request.Type))
            return new(DecisionKind.Denied, $"{request.Type} is never automatic: what it protects would be lost for good");

        var mode = (AutomationMode)Math.Min((int)ModeOf(automation), (int)options.MaxMode);
        if (mode != AutomationMode.Apply)
            return new(DecisionKind.Withheld, $"automation mode {mode}: the action is planned, not run");

        if (request.Risk != RiskClass.Destructive)
            return new(DecisionKind.Auto, $"{request.Risk} action in Apply mode");
        if (intent == ActuationIntent.ResourceDeletion)
            return new(DecisionKind.Auto, "cleanup of a resource being deleted");
        if (intent == ActuationIntent.SpecDeclared)
            return new(DecisionKind.Auto, "declared by the spec");
        if (intent == ActuationIntent.HumanAnnotation)
            return new(DecisionKind.Approved, "approved by the resource's own approval annotation");
        if (approvals.Contains(request.Id))
            return new(DecisionKind.Approved, $"approved by {ActuationGateway.ApproveAnnotation}");
        if (automation?.PreAuthorized?.Contains(request.Type.ToString(), StringComparer.OrdinalIgnoreCase) == true)
        {
            var now = clock.GetUtcNow();
            if (_lastDestructive.TryGetValue(owner, out var last) && now - last < TimeSpan.FromHours(1))
                return new(DecisionKind.RequiresApproval, $"pre-authorized, but the budget of one destructive action an hour is spent (last {last:O})");
            while (_destructiveRuns.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1))
                _destructiveRuns.TryDequeue(out _);
            if (_destructiveRuns.Count >= options.MaxDestructivePerHour)
                return new(DecisionKind.RequiresApproval, $"pre-authorized, but the operator-wide budget of {options.MaxDestructivePerHour} destructive actions an hour is spent");
            return new(DecisionKind.Auto, "pre-authorized by spec.automation.preAuthorized, within budget");
        }
        return new(DecisionKind.RequiresApproval, $"destructive: annotate {ActuationGateway.ApproveAnnotation}={request.Id} to run it");
    }

    /// <summary>Spends the budget of a destructive action that ran on its own.</summary>
    public void Spent(ActionRequest request, string owner, PolicyDecision decision)
    {
        if (request.Risk != RiskClass.Destructive || decision.Kind != DecisionKind.Auto)
            return;
        var now = clock.GetUtcNow();
        _lastDestructive[owner] = now;
        _destructiveRuns.Enqueue(now);
    }
}
