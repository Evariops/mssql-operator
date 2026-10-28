namespace MsSqlOperator.Entities.Common;

/// <summary>
/// What the operator may do on its own for this resource. Its disruptive and destructive
/// actions go through the actuation gateway, which decides — under this mode, capped by
/// Operator:Automation:MaxMode — whether to act, only to plan, or to wait for an approval.
/// </summary>
public class AutomationSpec
{
    /// <summary>Observe (decide and report, act on nothing), Plan (also expose the plan:
    /// <c>kubectl get operatorplans</c>) or Apply (act). Default Apply: the existing behaviour.</summary>
    public string Mode { get; set; } = "Apply";

    /// <summary>Destructive action types the operator may run without an approval, within its
    /// budget (one per resource an hour). Without it a destructive action waits for
    /// <c>mssql-operator.evariops.com/approve: &lt;id&gt;</c>.</summary>
    public List<string>? PreAuthorized { get; set; }
}
