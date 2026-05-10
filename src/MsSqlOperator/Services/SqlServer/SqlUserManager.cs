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

    public async Task DisableLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER LOGIN [{loginName}] DISABLE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnableLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER LOGIN [{loginName}] ENABLE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> KillSessionsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        var sessionIds = new List<int>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT session_id FROM sys.dm_exec_sessions WHERE login_name = @name AND session_id != @@SPID";
            cmd.Parameters.AddWithValue("@name", loginName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sessionIds.Add(reader.GetInt32(0));
        }

        foreach (var sid in sessionIds)
        {
            await using var killCmd = connection.CreateCommand();
            killCmd.CommandText = $"KILL {sid}";
            try { await killCmd.ExecuteNonQueryAsync(ct); }
            catch { /* session may have ended */ }
        }

        return sessionIds.Count;
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

    public async Task AddServerRoleMemberAsync(SqlConnection connection, string loginName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER SERVER ROLE [{roleName}] ADD MEMBER [{loginName}]";
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

    public async Task TransferSchemaOwnershipAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE [{database}];
            DECLARE @schema NVARCHAR(128);
            DECLARE cur CURSOR FOR
                SELECT s.name FROM sys.schemas s
                INNER JOIN sys.database_principals p ON s.principal_id = p.principal_id
                WHERE p.name = @name;
            OPEN cur;
            FETCH NEXT FROM cur INTO @schema;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC('ALTER AUTHORIZATION ON SCHEMA::' + QUOTENAME(@schema) + ' TO [dbo]');
                FETCH NEXT FROM cur INTO @schema;
            END;
            CLOSE cur;
            DEALLOCATE cur;
            """;
        cmd.Parameters.AddWithValue("@name", userName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
