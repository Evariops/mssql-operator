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
        // T-SQL DDL does not accept a query parameter for the PASSWORD literal
        // (CREATE LOGIN ... WITH PASSWORD = @p fails: "Incorrect syntax near '@p'").
        // The password must be an escaped string literal; QuoteStringLiteral doubles
        // embedded single quotes so this stays injection-safe.
        var sidClause = sid != null ? $", SID = 0x{Convert.ToHexString(sid)}" : "";
        var dbClause = defaultDatabase != null ? $", DEFAULT_DATABASE = {SqlQuoting.QuoteIdentifier(defaultDatabase)}" : "";
        cmd.CommandText = $"""
            CREATE LOGIN {SqlQuoting.QuoteIdentifier(loginName)} WITH PASSWORD = {SqlQuoting.QuoteNStringLiteral(password)},
            CHECK_POLICY = {(checkPolicy ? "ON" : "OFF")},
            CHECK_EXPIRATION = {(checkExpiration ? "ON" : "OFF")}
            {dbClause}{sidClause}
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AlterLoginPasswordAsync(SqlConnection connection, string loginName, string password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // See CreateLoginAsync: the password must be an escaped literal, not a parameter.
        cmd.CommandText = $"ALTER LOGIN {SqlQuoting.QuoteIdentifier(loginName)} WITH PASSWORD = {SqlQuoting.QuoteNStringLiteral(password)}";
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
        cmd.CommandText = $"ALTER LOGIN {SqlQuoting.QuoteIdentifier(loginName)} DISABLE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnableLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER LOGIN {SqlQuoting.QuoteIdentifier(loginName)} ENABLE";
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
            catch (SqlException) { /* the session may have ended meanwhile */ }
        }

        return sessionIds.Count;
    }

    public async Task<bool> DatabaseUserExistsAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SqlQuoting.QuoteIdentifier(database)}.sys.database_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", userName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateDatabaseUserAsync(SqlConnection connection, string database, string userName, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; CREATE USER {SqlQuoting.QuoteIdentifier(userName)} FOR LOGIN {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddRoleMemberAsync(SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; ALTER ROLE {SqlQuoting.QuoteIdentifier(roleName)} ADD MEMBER {SqlQuoting.QuoteIdentifier(userName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantSchemaPermissionAsync(SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; GRANT {SqlQuoting.ValidateSchemaPermission(permission)} ON SCHEMA::{SqlQuoting.QuoteIdentifier(schema)} TO {SqlQuoting.QuoteIdentifier(userName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantServerPermissionAsync(SqlConnection connection, string loginName, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"GRANT {SqlQuoting.ValidateServerPermission(permission)} TO {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddServerRoleMemberAsync(SqlConnection connection, string loginName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER SERVER ROLE {SqlQuoting.QuoteIdentifier(roleName)} ADD MEMBER {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropServerRoleMemberAsync(SqlConnection connection, string loginName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER SERVER ROLE {SqlQuoting.QuoteIdentifier(roleName)} DROP MEMBER {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeServerPermissionAsync(SqlConnection connection, string loginName, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"REVOKE {SqlQuoting.ValidateServerPermission(permission)} FROM {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropRoleMemberAsync(SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; ALTER ROLE {SqlQuoting.QuoteIdentifier(roleName)} DROP MEMBER {SqlQuoting.QuoteIdentifier(userName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeSchemaPermissionAsync(SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; REVOKE {SqlQuoting.ValidateSchemaPermission(permission)} ON SCHEMA::{SqlQuoting.QuoteIdentifier(schema)} FROM {SqlQuoting.QuoteIdentifier(userName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PasswordMatchesAsync(SqlConnection connection, string loginName, string password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PWDCOMPARE(@password, password_hash) FROM sys.sql_logins WHERE name = @name";
        cmd.Parameters.AddWithValue("@password", password);
        cmd.Parameters.AddWithValue("@name", loginName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    public async Task RenameLoginAsync(SqlConnection connection, string oldName, string newName, CancellationToken ct = default)
    {
        // login.name mutation is orchestrated — ALTER LOGIN
        // WITH NAME is a native rename preserving the SID, so database users stay mapped.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER LOGIN {SqlQuoting.QuoteIdentifier(oldName)} WITH NAME = {SqlQuoting.QuoteIdentifier(newName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP LOGIN {SqlQuoting.QuoteIdentifier(loginName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropDatabaseUserAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"USE {SqlQuoting.QuoteIdentifier(database)}; DROP USER {SqlQuoting.QuoteIdentifier(userName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool?> IsLoginEnabledAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT is_disabled FROM sys.server_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", loginName);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result == DBNull.Value) return null;
        return Convert.ToInt32(result) == 0;
    }

    public async Task<List<string>> GetServerRoleMembershipsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.name
            FROM sys.server_role_members m
            JOIN sys.server_principals r ON m.role_principal_id = r.principal_id
            JOIN sys.server_principals u ON m.member_principal_id = u.principal_id
            WHERE u.name = @name
            ORDER BY r.name
            """;
        cmd.Parameters.AddWithValue("@name", loginName);
        return await ReadStringListAsync(cmd, ct);
    }

    public async Task<List<string>> GetDatabaseRoleMembershipsAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE {SqlQuoting.QuoteIdentifier(database)};
            SELECT r.name
            FROM sys.database_role_members m
            JOIN sys.database_principals r ON m.role_principal_id = r.principal_id
            JOIN sys.database_principals u ON m.member_principal_id = u.principal_id
            WHERE u.name = @name
            ORDER BY r.name
            """;
        cmd.Parameters.AddWithValue("@name", userName);
        return await ReadStringListAsync(cmd, ct);
    }

    public async Task<Dictionary<string, List<string>>> GetDatabaseUserRolesAsync(SqlConnection connection,
        IReadOnlyCollection<string> databases, string userName, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (databases.Count == 0)
            return result;

        // One round-trip for every database (a user row, LEFT JOINed to its roles).
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.Join("\nUNION ALL\n", databases.Select(db =>
        {
            var name = SqlQuoting.QuoteNStringLiteral(db);
            var qdb = SqlQuoting.QuoteIdentifier(db);
            return $"""
                SELECT {name}, r.name
                FROM {qdb}.sys.database_principals u
                LEFT JOIN {qdb}.sys.database_role_members m ON m.member_principal_id = u.principal_id
                LEFT JOIN {qdb}.sys.database_principals r ON r.principal_id = m.role_principal_id
                WHERE u.name = @name
                """;
        }));
        cmd.Parameters.AddWithValue("@name", userName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var roles = result.TryGetValue(reader.GetString(0), out var existing) ? existing : result[reader.GetString(0)] = [];
            if (!reader.IsDBNull(1))
                roles.Add(reader.GetString(1));
        }

        foreach (var roles in result.Values)
            roles.Sort(StringComparer.Ordinal);
        return result;
    }

    private static async Task<List<string>> ReadStringListAsync(SqlCommand cmd, CancellationToken ct)
    {
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task TransferSchemaOwnershipAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            USE {SqlQuoting.QuoteIdentifier(database)};
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

    public async Task<int> GetRollbacksInProgressAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE command = N'KILLED/ROLLBACK'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
