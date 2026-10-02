namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Deployment marker stored on each deployed procedure as an extended property:
/// <c>v1|{namespace}/{name}|{content hash}|{definition hash}</c>. The owner lets the finalizer drop only
/// the procedures this resource deployed — it used to drop every procedure of the same name in every
/// filtered database. The two hashes let a pass skip a procedure that is unchanged on both sides
/// instead of re-running CREATE OR ALTER (recompilation, Sch-M lock) on every resync, while a
/// procedure edited by hand (its definition hash moved) is still redeployed.
/// </summary>
public sealed record ProcedureMarker(string Owner, string SpecHash, string DefinitionHash)
{
    public const string PropertyName = "mssql-operator.evariops.com/deployment";
    private const string Version = "v1";

    /// <summary>The marker value up to the definition hash, which SQL Server appends itself.</summary>
    public static string Prefix(string owner, string specHash) => $"{Version}|{owner}|{specHash}|";

    public static ProcedureMarker? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var parts = value.Split('|');
        return parts is [Version, var owner, var spec, var definition] ? new ProcedureMarker(owner, spec, definition) : null;
    }

    /// <summary>True when the procedure in the database is exactly what this owner deployed from
    /// this content.</summary>
    public bool IsCurrent(string owner, string specHash, string? currentDefinitionHash)
        => Owner == owner
           && SpecHash == specHash
           && !string.IsNullOrEmpty(currentDefinitionHash)
           && string.Equals(DefinitionHash, currentDefinitionHash, StringComparison.OrdinalIgnoreCase);
}
