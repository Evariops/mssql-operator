using System.Text.RegularExpressions;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Shared validation rules for SQL identifiers fed into DDL from CR specs.
/// Defense in depth in front of <see cref="SqlQuoting"/>: the webhook rejects exotic names
/// early with an actionable message, but quoting stays mandatory — the webhook can be disabled.
/// </summary>
public static partial class SqlIdentifierRules
{
    // Restrictive by default — letters, digits, underscore, hyphen;
    // no leading digit, no spaces, no dots. Widening later is non-breaking; narrowing is not.
    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_\-]*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Returns an actionable error message, or null when the identifier is acceptable.</summary>
    public static string? Check(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{field} is required";
        if (value.Length > 128)
            return $"{field} exceeds 128 characters (sysname limit)";
        if (value.Any(char.IsControl))
            return $"{field} contains control characters";
        if (!IdentifierPattern().IsMatch(value))
            return $"{field} '{value}' is not an accepted SQL identifier: letters, digits, underscore and hyphen only, not starting with a digit";
        return null;
    }

    /// <summary>
    /// Logins the operator itself depends on, or that SQL Server reserves.
    ///
    /// A SqlUser targeting <c>sa</c> passed every check and the controller dutifully ran
    /// <c>ALTER LOGIN [sa] WITH PASSWORD = …</c> with the CR's own secret: the operator — which
    /// connects as <c>sa</c> — instantly locked itself out of the instance. Health checks, failover,
    /// every reconcile stopped working until a human fixed the password by hand. The expiration path
    /// (action: Disable) did the same thing by disabling the login outright.
    /// </summary>
    public static readonly HashSet<string> ReservedLoginNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sa",
        "mssqlop_proc_deployer", // the operator's least-privilege stored-procedure deployer
    };

    /// <summary>True when the login name is reserved for SQL Server or the operator itself.</summary>
    public static bool IsReservedLogin(string? name)
        => !string.IsNullOrEmpty(name)
           && (ReservedLoginNames.Contains(name)
               || name.StartsWith("##", StringComparison.Ordinal)          // ##MS_* certificate logins
               || name.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Actionable rejection message for a reserved login, or null.</summary>
    public static string? CheckNotReserved(string? name, string field)
        => IsReservedLogin(name)
            ? $"{field} is reserved ({name}): it is used by SQL Server or by the operator itself. " +
              "Managing it through a SqlUser would let the operator lock itself out of the instance " +
              "(it authenticates with this login) — refused."
            : null;

    /// <summary>Like <see cref="Check"/> but accepts absent/empty values (optional fields).</summary>
    public static string? CheckOptional(string? value, string field)
        => string.IsNullOrEmpty(value) ? null : Check(value, field);

    /// <summary>The fixed server roles of SQL Server — the only values serverRoles[] accepts.</summary>
    public static readonly HashSet<string> FixedServerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysadmin", "securityadmin", "serveradmin", "setupadmin",
        "processadmin", "diskadmin", "dbcreator", "bulkadmin",
    };

    /// <summary>
    /// Server roles that require an explicit opt-in annotation on the *target*
    /// SqlInstance/SqlAvailabilityGroup. Every fixed server role
    /// is instance-wide power a namespace tenant must not hand itself — sysadmin/securityadmin/
    /// serveradmin are a takeover, bulkadmin reads any file the sqlservr process can read (the mounted
    /// machine key included), setupadmin manages linked servers, processadmin kills any session,
    /// diskadmin manages backup devices, dbcreator drops or restores any database.
    /// </summary>
    public static readonly HashSet<string> PrivilegedServerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysadmin", "securityadmin", "serveradmin",
        "bulkadmin", "setupadmin", "processadmin", "diskadmin", "dbcreator",
    };

    /// <summary>
    /// Server permissions under the same target opt-in as <see cref="PrivilegedServerRoles"/>:
    /// equivalent to sysadmin (CONTROL SERVER), to any login (IMPERSONATE ANY LOGIN, ALTER ANY LOGIN,
    /// ALTER ANY SERVER ROLE), to reading every file or every row (ADMINISTER BULK OPERATIONS, SELECT
    /// ALL USER SECURABLES, ALTER TRACE), or to stopping and reshaping the instance. Only the
    /// read-only and connect permissions stay ungated.
    /// </summary>
    public static readonly HashSet<string> PrivilegedServerPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONTROL SERVER", "IMPERSONATE ANY LOGIN", "ALTER ANY LOGIN", "ALTER ANY SERVER ROLE",
        "ADMINISTER BULK OPERATIONS", "SELECT ALL USER SECURABLES", "ALTER TRACE", "SHUTDOWN",
        "ALTER ANY DATABASE", "ALTER ANY LINKED SERVER", "ALTER ANY CONNECTION", "ALTER ANY ENDPOINT",
        "ALTER SERVER STATE",
    };

    public const string AllowPrivilegedRolesAnnotation = "mssql-operator.evariops.com/allow-privileged-roles";

    /// <summary>Target opt-in for <c>deployAs: Sa</c> stored-procedure deployments.</summary>
    public const string AllowSaDeploymentsAnnotation = "mssql-operator.evariops.com/allow-sa-deployments";

    public static bool AllowsPrivilegedRoles(IDictionary<string, string>? annotations)
        => AnnotationEnabled(annotations, AllowPrivilegedRolesAnnotation);

    /// <summary>True when <paramref name="annotation"/> is set to "true" (case-insensitive).</summary>
    public static bool AnnotationEnabled(IDictionary<string, string>? annotations, string annotation)
        => annotations != null
           && annotations.TryGetValue(annotation, out var v)
           && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
}
