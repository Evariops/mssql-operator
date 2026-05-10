using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlUserSpec
{
    public List<UserTarget>? Targets { get; set; }
    public LoginSpec? Login { get; set; }
    public ExpirationSpec? Expiration { get; set; }
    public List<DatabasePermissionSpec>? DatabasePermissions { get; set; }
    public List<string>? ServerRoles { get; set; }
    public List<string>? ServerPermissions { get; set; }
}

public class UserTarget
{
    public ResourceReference? InstanceRef { get; set; }
    public ResourceReference? AvailabilityGroupRef { get; set; }
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
