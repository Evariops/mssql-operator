namespace MsSqlOperator.Services.SqlServer;

public interface ISqlUserManager
{
    /// <summary>For each database where <paramref name="userName"/> exists, its role memberships —
    /// one round-trip for all of them. A database without the user is absent.</summary>
    Task<Dictionary<string, List<string>>> GetDatabaseUserRolesAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        IReadOnlyCollection<string> databases, string userName, CancellationToken ct = default);

    Task<bool> LoginExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task CreateLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string password, string? defaultDatabase = null, bool checkPolicy = true, bool checkExpiration = false, byte[]? sid = null, CancellationToken ct = default);
    Task AlterLoginPasswordAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string password, CancellationToken ct = default);
    Task<byte[]?> GetLoginSidAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DisableLogin)]
    Task DisableLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task EnableLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.KillSessions)]
    Task<int> KillSessionsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    /// <summary>Returns whether the login is enabled (true) or disabled (false); null if it does not exist. Read-only.</summary>
    Task<bool?> IsLoginEnabledAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    /// <summary>Live server-role memberships of a login (e.g. sysadmin). Read-only.</summary>
    Task<List<string>> GetServerRoleMembershipsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    /// <summary>Live database-role memberships of a database user. Read-only.</summary>
    Task<List<string>> GetDatabaseRoleMembershipsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
    Task<bool> DatabaseUserExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
    Task CreateDatabaseUserAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string loginName, CancellationToken ct = default);
    Task AddRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default);
    Task GrantSchemaPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default);
    Task GrantServerPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string permission, CancellationToken ct = default);
    Task AddServerRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string roleName, CancellationToken ct = default);

    // ── Revocation of grants removed from the spec ──
    Task DropServerRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string roleName, CancellationToken ct = default);
    Task RevokeServerPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string permission, CancellationToken ct = default);
    Task DropRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default);
    Task RevokeSchemaPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default);

    /// <summary>True when <paramref name="password"/> is the login's current password (PWDCOMPARE):
    /// lets the reconcile skip an ALTER LOGIN that would reset the password age.</summary>
    Task<bool> PasswordMatchesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string password, CancellationToken ct = default);
    /// <summary>Native rename: SID and database-user mappings are preserved.</summary>
    Task RenameLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string oldName, string newName, CancellationToken ct = default);

    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DropLogin)]
    Task DropLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    [MsSqlOperator.Services.Actuation.Destructive(MsSqlOperator.Services.Actuation.ActionType.DropDatabaseUser)]
    Task DropDatabaseUserAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
    Task TransferSchemaOwnershipAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
}
