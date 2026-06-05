namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Centralized SQL identifier and literal quoting to prevent injection and syntax errors.
/// </summary>
public static class SqlQuoting
{
    /// <summary>
    /// Quotes a SQL identifier using bracket notation, escaping embedded ] characters.
    /// Example: "my]name" → "[my]]name]"
    /// </summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]")}]";
    }

    /// <summary>
    /// Quotes a SQL string literal, escaping embedded ' characters.
    /// Example: "it's" → "'it''s'"
    /// </summary>
    public static string QuoteStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>
    /// Quotes an N-prefixed SQL string literal for Unicode values.
    /// Example: "café" → "N'café'"
    /// </summary>
    public static string QuoteNStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"N'{value.Replace("'", "''")}'";
    }

    private static readonly HashSet<string> AllowedServerPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "VIEW ANY DATABASE", "VIEW ANY DEFINITION", "VIEW SERVER STATE",
        "CONNECT SQL", "ALTER ANY DATABASE", "ALTER ANY LOGIN",
        "ALTER ANY SERVER ROLE", "ALTER ANY LINKED SERVER",
        "ALTER ANY CONNECTION", "ALTER ANY ENDPOINT",
        "ALTER SERVER STATE", "ALTER TRACE",
        "CREATE ANY DATABASE", "CONNECT ANY DATABASE",
        "IMPERSONATE ANY LOGIN", "SELECT ALL USER SECURABLES",
        "ADMINISTER BULK OPERATIONS", "SHUTDOWN",
        "CONTROL SERVER"
    };

    private static readonly HashSet<string> AllowedSchemaPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE",
        "ALTER", "CONTROL", "REFERENCES", "VIEW DEFINITION",
        "CREATE SEQUENCE", "TAKE OWNERSHIP", "VIEW CHANGE TRACKING"
    };

    private static readonly HashSet<string> AllowedTdeAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AES_128", "AES_192", "AES_256", "TRIPLE_DES_3KEY"
    };

    /// <summary>
    /// Validates a server permission against an allowlist.
    /// </summary>
    public static string ValidateServerPermission(string permission)
    {
        if (!AllowedServerPermissions.Contains(permission))
            throw new ArgumentException($"Server permission '{permission}' is not in the allowed list.");
        return permission;
    }

    /// <summary>
    /// Validates a schema permission against an allowlist.
    /// </summary>
    public static string ValidateSchemaPermission(string permission)
    {
        if (!AllowedSchemaPermissions.Contains(permission))
            throw new ArgumentException($"Schema permission '{permission}' is not in the allowed list.");
        return permission;
    }

    /// <summary>
    /// Validates a TDE algorithm against an allowlist.
    /// </summary>
    public static string ValidateTdeAlgorithm(string algorithm)
    {
        if (!AllowedTdeAlgorithms.Contains(algorithm))
            throw new ArgumentException($"TDE algorithm '{algorithm}' is not in the allowed list.");
        return algorithm;
    }

    /// <summary>
    /// Validates a date string format (yyyy-MM-dd) for use in SQL EXPIRY_DATE.
    /// </summary>
    public static string ValidateDateLiteral(string date)
    {
        if (!DateTime.TryParseExact(date, ["yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _))
            throw new ArgumentException($"Date '{date}' is not a valid date format (expected yyyy-MM-dd).");
        return date;
    }

    /// <summary>
    /// Builds a properly escaped FQDN for a StatefulSet pod.
    /// </summary>
    public static string BuildAgPodFqdn(string agName, int ordinal, string headlessService, string ns)
    {
        return $"{agName}-{ordinal}.{headlessService}.{ns}.svc.cluster.local";
    }

    /// <summary>
    /// Builds a properly escaped FQDN for a StatefulSet pod using AG name as headless service base.
    /// </summary>
    public static string BuildAgPodFqdn(string agName, int ordinal, string ns)
    {
        var headlessService = $"{agName}-headless";
        return BuildAgPodFqdn(agName, ordinal, headlessService, ns);
    }
}
