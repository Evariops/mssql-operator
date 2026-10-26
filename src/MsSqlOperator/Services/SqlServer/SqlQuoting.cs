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
        // sysname is nvarchar(128); embedded control characters are never legitimate in an
        // identifier the operator manages. Failing here gives an actionable error instead of
        // an unreadable SQL syntax error downstream.
        if (identifier.Length > 128)
            throw new ArgumentException($"SQL identifier exceeds 128 characters (sysname limit): '{identifier[..32]}…' ({identifier.Length} chars).", nameof(identifier));
        if (identifier.Any(char.IsControl))
            throw new ArgumentException("SQL identifier contains control characters.", nameof(identifier));
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

    // TRIPLE_DES_3KEY dropped — 3DES is deprecated (64-bit blocks, Sweet32).
    private static readonly HashSet<string> AllowedTdeAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AES_128", "AES_192", "AES_256"
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

    private static readonly HashSet<string> AllowedAvailabilityModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYNCHRONOUS_COMMIT", "ASYNCHRONOUS_COMMIT"
    };

    private static readonly HashSet<string> AllowedSeedingModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUTOMATIC", "MANUAL"
    };

    private static readonly HashSet<string> AllowedSecondaryAllowConnections = new(StringComparer.OrdinalIgnoreCase)
    {
        "NO", "READ_ONLY", "ALL"
    };

    // RC4 (and the combinations that let a peer negotiate it) dropped — RC4 is broken.
    private static readonly HashSet<string> AllowedEndpointAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AES"
    };

    /// <summary>
    /// Validates an AG replica availability mode against an allowlist.
    /// </summary>
    public static string ValidateAvailabilityMode(string mode)
    {
        if (!AllowedAvailabilityModes.Contains(mode))
            throw new ArgumentException($"Availability mode '{mode}' is not in the allowed list.");
        return mode;
    }

    /// <summary>
    /// Validates an AG replica seeding mode against an allowlist.
    /// </summary>
    public static string ValidateSeedingMode(string mode)
    {
        if (!AllowedSeedingModes.Contains(mode))
            throw new ArgumentException($"Seeding mode '{mode}' is not in the allowed list.");
        return mode;
    }

    /// <summary>
    /// Validates a SECONDARY_ROLE ALLOW_CONNECTIONS value against an allowlist.
    /// </summary>
    public static string ValidateAllowConnections(string value)
    {
        if (!AllowedSecondaryAllowConnections.Contains(value))
            throw new ArgumentException($"ALLOW_CONNECTIONS value '{value}' is not in the allowed list.");
        return value;
    }

    /// <summary>
    /// Validates a database mirroring endpoint encryption algorithm against an allowlist.
    /// </summary>
    public static string ValidateEndpointAlgorithm(string algorithm)
    {
        if (!AllowedEndpointAlgorithms.Contains(algorithm))
            throw new ArgumentException($"Endpoint algorithm '{algorithm}' is not in the allowed list.");
        return algorithm;
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

    /// <summary>Date formats accepted for a certificate EXPIRY_DATE — the same set the admission
    /// webhook accepts (the webhook allowed a time part that this method then rejected, so
    /// CREATE CERTIFICATE failed at runtime).</summary>
    public static readonly string[] AcceptedDateFormats =
        ["yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss"];

    /// <summary>
    /// Validates a date for use in SQL EXPIRY_DATE and returns it in a format SQL Server reads the
    /// same way whatever the session's DATEFORMAT/language: <c>yyyyMMdd</c>, or ISO 8601
    /// <c>yyyy-MM-ddTHH:mm:ss</c> (UTC) when a time is given. (<c>yyyy-MM-dd</c> is read as
    /// year-day-month under <c>SET DATEFORMAT dmy</c>.)
    /// </summary>
    public static string ValidateDateLiteral(string date)
    {
        if (!DateTimeOffset.TryParseExact(date, AcceptedDateFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            throw new ArgumentException($"Date '{date}' is not a valid date format (expected yyyy-MM-dd or yyyy-MM-ddTHH:mm:ssZ).");
        var utc = parsed.UtcDateTime;
        return utc.TimeOfDay == TimeSpan.Zero
            ? utc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
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
