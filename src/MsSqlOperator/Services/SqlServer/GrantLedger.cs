namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// The grants a SqlUser made on one target, persisted in <c>status.appliedGrants</c> as
/// <c>{kind}/{target}|{grant kind}|{value}</c>. Without it, removing a role, a permission or a
/// database from the spec revoked nothing: privileges only ever accumulated and SQL Server drifted
/// away from the declaration. Only what the operator itself granted is recorded, so a grant made by
/// a DBA is never revoked.
/// </summary>
internal sealed class GrantLedger
{
    public const string ServerRole = "srole";
    public const string ServerPermission = "sperm";
    public const string DatabaseUser = "duser";
    public const string DatabaseRole = "drole";
    public const string SchemaPermission = "schema";

    private const char Separator = '|';

    private readonly string _target;
    private readonly Dictionary<string, HashSet<string>> _byKind = new(StringComparer.Ordinal);

    private GrantLedger(string target) => _target = target;

    /// <summary>The ledger of <paramref name="target"/> as recorded in the status entries.</summary>
    public static GrantLedger For(IEnumerable<string>? entries, string target)
    {
        var ledger = new GrantLedger(target);
        var prefix = target + Separator;
        foreach (var entry in entries ?? [])
        {
            if (!entry.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = entry[prefix.Length..];
            var cut = rest.IndexOf(Separator);
            if (cut > 0 && cut < rest.Length - 1)
                ledger.Add(rest[..cut], rest[(cut + 1)..]);
        }

        return ledger;
    }

    public bool HasDatabaseGrants => Count(DatabaseUser) + Count(DatabaseRole) + Count(SchemaPermission) > 0;

    public void Add(string kind, string value) => Set(kind).Add(value);

    public void AddRange(string kind, IEnumerable<string> values) => Set(kind).UnionWith(values);

    public bool Contains(string kind, string value) => _byKind.TryGetValue(kind, out var set) && set.Contains(value);

    /// <summary>The values recorded for <paramref name="kind"/>.</summary>
    public IReadOnlyCollection<string> Values(string kind) => _byKind.TryGetValue(kind, out var set) ? set : [];

    /// <summary>The databases the database-level entries name.</summary>
    public HashSet<string> Databases()
        => Values(DatabaseUser)
            .Concat(Values(DatabaseRole).Select(v => Split2(v).Database))
            .Concat(Values(SchemaPermission).Select(v => Split3(v).Database))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recorded values of <paramref name="kind"/> that are no longer desired.</summary>
    public List<string> Stale(string kind, IEnumerable<string> desired)
    {
        if (!_byKind.TryGetValue(kind, out var set))
            return [];
        var wanted = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        return set.Where(v => !wanted.Contains(v)).ToList();
    }

    /// <summary>Once grants and revocations succeeded: the recorded values are exactly the desired ones.</summary>
    public void Replace(string kind, IEnumerable<string> values)
    {
        var set = Set(kind);
        set.Clear();
        set.UnionWith(values);
    }

    /// <summary>The status list: entries of other targets untouched, this target's replaced; sorted
    /// so the status stays byte-stable across passes.</summary>
    public List<string>? Merge(IEnumerable<string>? entries)
    {
        var prefix = _target + Separator;
        var merged = (entries ?? []).Where(e => !e.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var (kind, set) in _byKind)
            merged.AddRange(set.Select(v => $"{prefix}{kind}{Separator}{v}"));
        merged.Sort(StringComparer.Ordinal);
        return merged.Count == 0 ? null : merged;
    }

    public static string Join(params string[] parts) => string.Join(Separator, parts);

    /// <summary>Splits <c>{database}|{name}</c> from the right: role names are validated identifiers
    /// (no separator), a database name read from the server might contain one.</summary>
    public static (string Database, string Name) Split2(string value)
    {
        var cut = value.LastIndexOf(Separator);
        return (value[..cut], value[(cut + 1)..]);
    }

    /// <summary>Splits <c>{database}|{schema}|{permission}</c> from the right.</summary>
    public static (string Database, string Schema, string Permission) Split3(string value)
    {
        var (head, permission) = Split2(value);
        var (database, schema) = Split2(head);
        return (database, schema, permission);
    }

    private int Count(string kind) => _byKind.TryGetValue(kind, out var set) ? set.Count : 0;

    private HashSet<string> Set(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var set))
            _byKind[kind] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return set;
    }
}
