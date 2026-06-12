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
    /// Roles amounting to a full instance takeover; granting them requires an explicit opt-in
    /// annotation on the *target* SqlInstance/SqlAvailabilityGroup.
    /// </summary>
    public static readonly HashSet<string> PrivilegedServerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysadmin", "securityadmin", "serveradmin",
    };

    public const string AllowPrivilegedRolesAnnotation = "mssql.operator/allow-privileged-roles";

    public static bool AllowsPrivilegedRoles(IDictionary<string, string>? annotations)
        => annotations != null
           && annotations.TryGetValue(AllowPrivilegedRolesAnnotation, out var v)
           && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
}
