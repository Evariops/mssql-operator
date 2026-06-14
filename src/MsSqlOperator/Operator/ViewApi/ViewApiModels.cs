using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsSqlOperator.Operator.ViewApi;

// Minimal Kubernetes API shapes for the aggregated, read-only `view.mssql-operator.evariops.com` group.
// These resources are NEVER stored in etcd: each request computes the answer live from SQL.
// See docs/specifications/16. JSON is camelCase, nulls omitted, matching the apiserver wire format.
public static class ViewApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

public sealed class ApiGroupList
{
    public string Kind => "APIGroupList";
    public string ApiVersion => "v1";
    public List<ApiGroup> Groups { get; init; } = [];
}

public sealed class ApiGroup
{
    public string Kind => "APIGroup";
    public string ApiVersion => "v1";
    public required string Name { get; init; }
    public List<GroupVersionForDiscovery> Versions { get; init; } = [];
    public GroupVersionForDiscovery? PreferredVersion { get; init; }
}

public sealed class GroupVersionForDiscovery
{
    public required string GroupVersion { get; init; }
    public required string Version { get; init; }
}

public sealed class ApiResourceList
{
    public string Kind => "APIResourceList";
    public string ApiVersion => "v1";
    public required string GroupVersion { get; init; }
    public List<ApiResource> Resources { get; init; } = [];
}

public sealed class ApiResource
{
    public required string Name { get; init; }
    public required string SingularName { get; init; }
    public bool Namespaced { get; init; } = true;
    public required string Kind { get; init; }
    public List<string> Verbs { get; init; } = ["get", "list"];
    public List<string>? ShortNames { get; init; }
}

// A generic list of view items. Items are JsonObject-like dictionaries so each provider can shape
// its own fields without a bespoke type per resource on the wire.
public sealed class ViewList
{
    public required string Kind { get; init; }
    public string ApiVersion => "view.mssql-operator.evariops.com/v1alpha1";
    public ListMeta Metadata { get; init; } = new();
    public List<Dictionary<string, object?>> Items { get; init; } = [];
}

public sealed class ListMeta
{
    // "0" = "potentially stale read" — with the TTL cache that semantic is now literally
    // true; the view API is not watchable.
    public string ResourceVersion => "0";

    /// <summary>Opaque continue token — the offset of the next page.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("continue")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinueToken { get; set; }
}

// ── Server-side printing (Table) for `kubectl get` ──
public sealed class Table
{
    public string Kind => "Table";
    public string ApiVersion => "meta.k8s.io/v1";
    public ListMeta Metadata { get; set; } = new();
    public List<TableColumnDefinition> ColumnDefinitions { get; init; } = [];
    public List<TableRow> Rows { get; init; } = [];
}

public sealed class TableColumnDefinition
{
    public required string Name { get; init; }
    public string Type { get; init; } = "string";
    public string Format { get; init; } = "";
    public string Description { get; init; } = "";
    public int Priority { get; init; }
}

public sealed class TableRow
{
    public List<object?> Cells { get; init; } = [];

    // Partial object metadata for this row. kubectl reads object.metadata.namespace to populate the
    // NAMESPACE column in `-A` (all-namespaces) output; without it that column renders blank.
    public object? Object { get; set; }
}
