using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// The one condition an on-call alerts on: True only when a human decision is
/// really required — a data-loss call, an AG with no writer and no automatic way out, a rollout
/// stopped on a failure. Everything else the operator heals on its own, and says so in events and
/// in the other conditions. Reasons are machine-readable.
/// </summary>
public static class NeedsAttention
{
    public const string Condition = "NeedsAttention";

    public static void Require(List<ConditionStatus> conditions, string reason, string message, DateTime? now = null)
        => ConditionOps.Set(conditions, Condition, "True", reason, message, now: now);

    public static void Clear(List<ConditionStatus> conditions, DateTime? now = null)
    {
        if (conditions.FirstOrDefault(c => c.Type == Condition) is { Status: "True" })
            ConditionOps.Set(conditions, Condition, "False", "SelfHealing",
                "No human decision is pending: the operator reconciles on its own.", now: now);
    }

    public static bool IsRequired(IEnumerable<ConditionStatus>? conditions)
        => conditions?.Any(c => c.Type == Condition && c.Status == "True") == true;
}
