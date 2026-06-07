namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// A read-only virtual resource in the aggregated <c>view.mssql.operator</c> group. Each request
/// computes the answer live from SQL Server (never etcd) — the spec-16 replacement for the per-item
/// detail that used to live in CRD status.
/// </summary>
public interface IViewResourceProvider
{
    /// <summary>Discovery descriptor (plural name, kind, short names, verbs).</summary>
    ApiResource Resource { get; }

    /// <summary>Columns for server-side <c>kubectl get</c> table printing.</summary>
    IReadOnlyList<TableColumnDefinition> Columns { get; }

    /// <summary>
    /// Lists items in a namespace. When <paramref name="name"/> is set, returns the single matching
    /// item (get). Implementations must tolerate unreachable targets (skip + continue), never throw
    /// for the whole list.
    /// </summary>
    Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct);

    /// <summary>Maps one item to a table row, in the same order as <see cref="Columns"/>.</summary>
    TableRow ToRow(Dictionary<string, object?> item);
}
