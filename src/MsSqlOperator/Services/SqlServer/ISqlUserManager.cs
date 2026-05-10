namespace MsSqlOperator.Services.SqlServer;

public interface ISqlUserManager
{
    Task<bool> LoginExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task CreateLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string password, string? defaultDatabase = null, bool checkPolicy = true, bool checkExpiration = false, byte[]? sid = null, CancellationToken ct = default);
    Task AlterLoginPasswordAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string password, CancellationToken ct = default);
    Task<byte[]?> GetLoginSidAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task DisableLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task EnableLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task<int> KillSessionsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task<bool> DatabaseUserExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
    Task CreateDatabaseUserAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string loginName, CancellationToken ct = default);
    Task AddRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default);
    Task GrantSchemaPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default);
    Task GrantServerPermissionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string permission, CancellationToken ct = default);
    Task AddServerRoleMemberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string roleName, CancellationToken ct = default);
    Task DropLoginAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task DropDatabaseUserAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
    Task TransferSchemaOwnershipAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string userName, CancellationToken ct = default);
}
