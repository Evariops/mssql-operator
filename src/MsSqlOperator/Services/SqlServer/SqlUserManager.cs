using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlUserManager : ISqlUserManager
{
    public async Task<bool> LoginExistsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.server_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", loginName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateLoginAsync(SqlConnection connection, string loginName, string password, string? defaultDatabase = null, bool checkPolicy = true, bool checkExpiration = false, byte[]? sid = null, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        var sidClause = sid != null ? $", SID = 0x{Convert.ToHexString(sid)}" : "";
        var dbClause = defaultDatabase != null ? $", DEFAULT_DATABASE = [{defaultDatabase}]" : "";
        cmd.CommandText = $"""
            CREATE LOGIN [{loginName}] WITH PASSWORD = @password,
            CHECK_POLICY = {(checkPolicy ? "ON" : "OFF")},
            CHECK_EXPIRATION = {(checkExpiration ? "ON" : "OFF")}
            {dbClause}{sidClause}
            """;
        cmd.Parameters.AddWithValue("@password", password);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AlterLoginPasswordAsync(SqlConnection connection, string loginName, string password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER LOGIN [{loginName}] WITH PASSWORD = @password";
        cmd.Parameters.AddWithValue("@password", password);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> GetLoginSidAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sid FROM sys.server_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", loginName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[];
    }

    public async Task<bool> DatabaseUserExistsAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{database}].sys.database_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", userName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateDatabaseUserAsync(SqlConnection connection, string database, string userName, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; CREATE USER [{userName}] FOR LOGIN [{loginName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddRoleMemberAsync(SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; ALTER ROLE [{roleName}] ADD MEMBER [{userName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantSchemaPermissionAsync(SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; GRANT {permission} ON SCHEMA::[{schema}] TO [{userName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantServerPermissionAsync(SqlConnection connection, string loginName, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"GRANT {permission} TO [{loginName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP LOGIN [{loginName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropDatabaseUserAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE [{database}]; DROP USER [{userName}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
