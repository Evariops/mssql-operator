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

    /// <summary>An extended property value holds at most 7 500 bytes: 3 500 characters per chunk.</summary>
    private const int GrantsChunk = 3500;

    public async Task<(string? OwnerUid, string? Grants)> ReadUserGrantsAsync(SqlConnection connection, string database, string userName,
        CancellationToken ct = default)
    {
        var db = SqlQuoting.QuoteIdentifier(database);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT ep.name, CAST(ep.value AS nvarchar(4000))
            FROM {db}.sys.extended_properties ep
            JOIN {db}.sys.database_principals p ON ep.class = 4 AND ep.major_id = p.principal_id
            WHERE p.name = @user AND ep.name LIKE N'mssqlop.%'
            """;
        cmd.Parameters.AddWithValue("@user", userName);
        string? owner = null;
        var chunks = new SortedDictionary<int, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (name == "mssqlop.owner")
                owner = value;
            else if (name == "mssqlop.grants")
                chunks[1] = value;
            else if (name.StartsWith("mssqlop.grants.", StringComparison.Ordinal)
                     && int.TryParse(name["mssqlop.grants.".Length..], System.Globalization.CultureInfo.InvariantCulture, out var index))
                chunks[index] = value;
        }
        return (owner, chunks.Count == 0 ? null : string.Concat(chunks.Values));
    }

    public async Task WriteUserGrantsAsync(SqlConnection connection, string database, string userName, string ownerUid, string? grants,
        CancellationToken ct = default)
    {
        var db = SqlQuoting.QuoteIdentifier(database);
        var chunks = grants is null ? [] : grants.Chunk(GrantsChunk).Select(c => new string(c)).ToList();
        var adds = new System.Text.StringBuilder();
        if (grants is not null)
            adds.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"EXEC {db}.sys.sp_addextendedproperty @name = N'mssqlop.owner', @value = @owner, @level0type = N'USER', @level0name = @user;\n");
        for (var i = 0; i < chunks.Count; i++)
            adds.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"EXEC {db}.sys.sp_addextendedproperty @name = @n{i}, @value = @c{i}, @level0type = N'USER', @level0name = @user;\n");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @name sysname;
            DECLARE props CURSOR LOCAL FAST_FORWARD FOR
                SELECT ep.name FROM {db}.sys.extended_properties ep
                JOIN {db}.sys.database_principals p ON ep.class = 4 AND ep.major_id = p.principal_id
                WHERE p.name = @user AND ep.name LIKE N'mssqlop.%';
            OPEN props;
            FETCH NEXT FROM props INTO @name;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC {db}.sys.sp_dropextendedproperty @name = @name, @level0type = N'USER', @level0name = @user;
                FETCH NEXT FROM props INTO @name;
            END
            CLOSE props;
            DEALLOCATE props;
            {adds}
            COMMIT;
            """;
        cmd.Parameters.AddWithValue("@user", userName);
        cmd.Parameters.AddWithValue("@owner", ownerUid);
        for (var i = 0; i < chunks.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@n{i}", i == 0 ? "mssqlop.grants" : $"mssqlop.grants.{i + 1}");
            cmd.Parameters.AddWithValue($"@c{i}", chunks[i]);
        }
        await cmd.ExecuteNonQueryAsync(ct);
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
