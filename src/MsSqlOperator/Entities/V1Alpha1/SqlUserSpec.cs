using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlUserSpec
{
    public TargetSelector Target { get; set; } = new();
    public LoginSpec? Login { get; set; }
    public ExpirationSpec? Expiration { get; set; }
    public List<DatabasePermissionSpec>? DatabasePermissions { get; set; }
    public List<string>? ServerRoles { get; set; }
    public List<string>? ServerPermissions { get; set; }
    public List<TargetOverride>? Overrides { get; set; }
}

public class TargetOverride
{
    public string? Instance { get; set; }
    public string? AvailabilityGroup { get; set; }
    public List<DatabasePermissionSpec>? DatabasePermissions { get; set; }
    public List<string>? ServerRoles { get; set; }
    public List<string>? ServerPermissions { get; set; }
}

public class ExpirationSpec
{
    public string? ExpiresAt { get; set; }
    public string? Ttl { get; set; }
    public string Action { get; set; } = "Disable";
    public bool TerminateSessions { get; set; } = true;
}

public class LoginSpec
{
    /// <summary>SQL login name. Restrictive charset.</summary>
    [System.ComponentModel.DataAnnotations.RegularExpression(@"^[\p{L}_][\p{L}\p{N}_\-]*$")]
    [System.ComponentModel.DataAnnotations.MaxLength(128)]
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "SqlLogin";
    public SecretReference? PasswordSecretRef { get; set; }
    public string? DefaultDatabase { get; set; }
    public bool CheckPolicy { get; set; } = true;
    public bool CheckExpiration { get; set; }
}

public class DatabasePermissionSpec
{
    public List<string>? Databases { get; set; }
    public DatabaseFilterSpec? DatabaseFilter { get; set; }
    public List<string>? Roles { get; set; }
    public List<SchemaPermissionSpec>? SchemaPermissions { get; set; }
}

public class SchemaPermissionSpec
{
    public string Schema { get; set; } = "dbo";
    public List<string> Permissions { get; set; } = [];
}
