using MsSqlOperator.Services.Actuation;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>operatorplans</c> — what the operator did, would do (Observe, Plan) or waits to be
/// allowed to do (an approval), per resource: the latest decisions of the actuation gateway, with
/// the id an approval names. Computed from memory on demand; nothing goes to etcd.
/// </summary>
public sealed class OperatorPlansProvider(PlanStore plans) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "operatorplans",
        SingularName = "operatorplan",
        Kind = "OperatorPlan",
        ShortNames = ["opplan"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "Resource", Type = "string" },
        new() { Name = "Action", Type = "string" },
        new() { Name = "Risk", Type = "string" },
        new() { Name = "Decision", Type = "string" },
        new() { Name = "Executed", Type = "boolean" },
        new() { Name = "Id", Type = "string" },
        new() { Name = "Reason", Type = "string", Priority = 1 },
        new() { Name = "Decided", Type = "string", Priority = 1 },
    ];

    public Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        foreach (var (key, actions) in plans.All())
        {
            var parts = key.Split('/');
            var (kind, ns, resource) = (parts[0], parts[1], parts[2]);
            if (@namespace is not null && ns != @namespace)
                continue;
            foreach (var action in actions)
                items.Add(new Dictionary<string, object?>
                {
                    ["apiVersion"] = "view.mssql-operator.evariops.com/v1alpha1",
                    ["kind"] = "OperatorPlan",
                    ["metadata"] = new Dictionary<string, object?>
                    {
                        ["name"] = ViewItems.Sanitize($"{resource}.{action.Type}.{action.Id.Split(':')[^1]}"),
                        ["namespace"] = ns,
                    },
                    ["resource"] = $"{kind}/{resource}",
                    ["action"] = action.Type,
                    ["risk"] = action.Risk,
                    ["decision"] = action.Decision,
                    ["executed"] = action.Executed,
                    ["id"] = action.Id,
                    ["reason"] = action.Reason,
                    ["target"] = action.Target,
                    ["description"] = action.Description,
                    ["decidedAt"] = action.DecidedAt.ToString("O"),
                });
        }
        return Task.FromResult(name is null ? items : items.Where(i => ViewItems.Name(i) == name).ToList());
    }

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells =
        [
            ViewItems.Name(item), item.GetValueOrDefault("resource"), item.GetValueOrDefault("action"),
            item.GetValueOrDefault("risk"), item.GetValueOrDefault("decision"), item.GetValueOrDefault("executed"),
            item.GetValueOrDefault("id"), item.GetValueOrDefault("reason"), item.GetValueOrDefault("decidedAt"),
        ]
    };
}
