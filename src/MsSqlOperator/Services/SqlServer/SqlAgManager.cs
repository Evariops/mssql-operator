using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlAgManager : ISqlAgManager
{
    public async Task EnableHadrAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_configure 'hadrenabled', 1; RECONFIGURE WITH OVERRIDE;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateEndpointAsync(SqlConnection connection, string endpointName, int port, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.endpoints WHERE name = @name)
            BEGIN
                CREATE ENDPOINT [{endpointName}]
                    STATE = STARTED
                    AS TCP (LISTENER_PORT = {port})
                    FOR DATABASE_MIRRORING (ROLE = ALL, AUTHENTICATION = CERTIFICATE [AG_Certificate], ENCRYPTION = REQUIRED ALGORITHM AES);
            END
            """;
        cmd.Parameters.AddWithValue("@name", endpointName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AvailabilityGroupExistsAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.availability_groups WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", agName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateAvailabilityGroupAsync(SqlConnection connection, string agName, List<string> databases, string seedingMode, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        var dbList = string.Join(", ", databases.Select(d => $"[{d}]"));
        cmd.CommandText = $"""
            CREATE AVAILABILITY GROUP [{agName}]
            WITH (AUTOMATED_BACKUP_PREFERENCE = SECONDARY, DB_FAILOVER = ON, CLUSTER_TYPE = NONE)
            FOR DATABASE {dbList}
            REPLICA ON N'@@SERVERNAME' WITH (
                ENDPOINT_URL = N'TCP://@@SERVERNAME:5022',
                AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
                FAILOVER_MODE = AUTOMATIC,
                SEEDING_MODE = {seedingMode},
                SECONDARY_ROLE (ALLOW_CONNECTIONS = ALL)
            );
            """.Replace("@@SERVERNAME", "' + @@SERVERNAME + N'");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task JoinAvailabilityGroupAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP [{agName}] JOIN WITH (CLUSTER_TYPE = NONE)";
        await cmd.ExecuteNonQueryAsync(ct);

        await using var cmd2 = connection.CreateCommand();
        cmd2.CommandText = $"ALTER AVAILABILITY GROUP [{agName}] GRANT CREATE ANY DATABASE";
        await cmd2.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPrimaryReplicaAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ars.role_desc
            FROM sys.dm_hadr_availability_replica_states ars
            JOIN sys.availability_groups ag ON ars.group_id = ag.group_id
            WHERE ag.name = @name AND ars.is_local = 1
            """;
        cmd.Parameters.AddWithValue("@name", agName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<List<(string name, string role, string syncState)>> GetReplicaStatesAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        var replicas = new List<(string, string, string)>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ar.replica_server_name, ars.role_desc, ars.synchronization_health_desc
            FROM sys.dm_hadr_availability_replica_states ars
            JOIN sys.availability_replicas ar ON ars.replica_id = ar.replica_id
            JOIN sys.availability_groups ag ON ar.group_id = ag.group_id
            WHERE ag.name = @name
            """;
        cmd.Parameters.AddWithValue("@name", agName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            replicas.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return replicas;
    }

    public async Task FailoverAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP [{agName}] FAILOVER";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SuspendDataMovementAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{database}] SET HADR SUSPEND";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResumeDataMovementAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{database}] SET HADR RESUME";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddDatabaseToAgAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP [{agName}] ADD DATABASE [{database}]";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveReplicaAsync(SqlConnection connection, string agName, string replicaName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP [{agName}] REMOVE REPLICA ON N'{replicaName}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
