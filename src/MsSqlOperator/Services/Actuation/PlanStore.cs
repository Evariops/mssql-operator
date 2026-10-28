using System.Collections.Concurrent;

namespace MsSqlOperator.Services.Actuation;

/// <summary>One decision of the gateway, as the plan shows it.</summary>
public sealed record PlannedAction(string Id, string Type, string Risk, string Target, string Description, string Decision,
    string Reason, bool Executed, DateTimeOffset DecidedAt);

/// <summary>
/// The latest plan of each resource, in memory and bounded — what the operator did, would
/// do, or waits to be allowed to do. Served by the view API (<c>kubectl get operatorplans</c>);
/// nothing goes to etcd.
/// </summary>
public sealed class PlanStore
{
    public const int MaxPerResource = 50;
    public const int MaxResources = 5000;

    private readonly ConcurrentDictionary<string, LinkedList<PlannedAction>> _plans = new();

    public static string Key(string kind, string? ns, string name) => $"{kind}/{ns}/{name}";

    public void Record(string key, PlannedAction action)
    {
        if (_plans.Count >= MaxResources && !_plans.ContainsKey(key))
            return;
        var plan = _plans.GetOrAdd(key, _ => new LinkedList<PlannedAction>());
        lock (plan)
        {
            for (var node = plan.First; node is not null; node = node.Next)
                if (node.Value.Id == action.Id)
                {
                    plan.Remove(node);
                    break;
                }
            plan.AddLast(action);
            while (plan.Count > MaxPerResource)
                plan.RemoveFirst();
        }
    }

    public IReadOnlyList<PlannedAction> Get(string key)
    {
        if (!_plans.TryGetValue(key, out var plan))
            return [];
        lock (plan) return plan.ToList();
    }

    public IReadOnlyList<(string Key, IReadOnlyList<PlannedAction> Actions)> All()
        => _plans.Keys.Order(StringComparer.Ordinal).Select(k => (k, Get(k))).ToList();

    public void Forget(string key) => _plans.TryRemove(key, out _);
}
