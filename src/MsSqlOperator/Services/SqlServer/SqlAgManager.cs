using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

public class SqlAgManager : ISqlAgManager
{
    // ── Infrastructure cryptographique ────────────────────

    public async Task<bool> MasterKeyExistsAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateMasterKeyAsync(SqlConnection connection, string password, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // PASSWORD must be an escaped string literal; a query parameter fails with
        // "Incorrect syntax near '@password'".
        cmd.CommandText = $"CREATE MASTER KEY ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteStringLiteral(password)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateHadrCertificateAsync(SqlConnection connection, string certName, string subject, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // SUBJECT in CREATE CERTIFICATE must be an escaped string literal, not a parameter.
        cmd.CommandText = $"CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)} WITH SUBJECT = {SqlQuoting.QuoteStringLiteral(subject)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ImportHadrCertificateAsync(SqlConnection connection, string certName, string certPath, string keyPath, string decryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // Paths and the DECRYPTION password are all literal clauses here — none can be parameterized.
        cmd.CommandText = $"""
            CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}
                FROM FILE = {SqlQuoting.QuoteStringLiteral(certPath)}
                WITH PRIVATE KEY (
                    FILE = {SqlQuoting.QuoteStringLiteral(keyPath)},
                    DECRYPTION BY PASSWORD = {SqlQuoting.QuoteStringLiteral(decryptionPassword)}
                )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ExportHadrCertificateAsync(SqlConnection connection, string certName, string certPath, string keyPath, string encryptionPassword, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            BACKUP CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}
                TO FILE = {SqlQuoting.QuoteStringLiteral(certPath)}
                WITH PRIVATE KEY (
                    FILE = {SqlQuoting.QuoteStringLiteral(keyPath)},
                    ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteStringLiteral(encryptionPassword)}
                )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> CertificateExistsAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.certificates WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", certName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task<string?> GetCertificateThumbprintAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(VARCHAR(256), thumbprint, 1) FROM sys.certificates WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", certName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<byte[]> ExportHadrPublicCertificateAsync(SqlConnection connection, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CERTENCODED(CERT_ID(@name))";
        cmd.Parameters.AddWithValue("@name", certName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[] ?? [];
    }

    public async Task ImportTrustedReplicaCertificateAsync(SqlConnection connection, string certName, byte[] publicCertBytes, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // CREATE CERTIFICATE FROM BINARY requires a binary constant (0x... literal); a query
        // parameter is rejected ("Incorrect syntax near '@certBytes'"). The bytes are an
        // operator-held public certificate, so a hex literal is safe.
        cmd.CommandText = $"CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)} FROM BINARY = 0x{Convert.ToHexString(publicCertBytes)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Logins ────────────────────────────────────────────

    public async Task<bool> LoginExistsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.server_principals WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", loginName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateEndpointLoginFromCertificateAsync(SqlConnection connection, string loginName, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE LOGIN {SqlQuoting.QuoteIdentifier(loginName)} FROM CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Endpoint HADR ─────────────────────────────────────

    public async Task<bool> EndpointExistsAsync(SqlConnection connection, string endpointName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.endpoints WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", endpointName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateEndpointAsync(SqlConnection connection, string endpointName, int port, string certName, string algorithm, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE ENDPOINT {SqlQuoting.QuoteIdentifier(endpointName)}
                STATE = STARTED
                AS TCP (LISTENER_PORT = {port}, LISTENER_IP = ALL)
                FOR DATABASE_MIRRORING (
                    ROLE = ALL,
                    AUTHENTICATION = CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)},
                    ENCRYPTION = REQUIRED ALGORITHM {SqlQuoting.ValidateEndpointAlgorithm(algorithm)}
                )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantEndpointConnectAsync(SqlConnection connection, string endpointName, string login, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"GRANT CONNECT ON ENDPOINT::{SqlQuoting.QuoteIdentifier(endpointName)} TO {SqlQuoting.QuoteIdentifier(login)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Availability Group lifecycle ──────────────────────

    public async Task<bool> AvailabilityGroupExistsAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.availability_groups WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", agName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task CreateAvailabilityGroupAsync(SqlConnection connection, AgTopologyConfig topology, CancellationToken ct = default)
    {
        var replicaClauses = topology.Replicas.Select(r =>
        {
            var clause = $"""
                {SqlQuoting.QuoteNStringLiteral(r.Name)} WITH (
                    ENDPOINT_URL = {SqlQuoting.QuoteNStringLiteral(r.EndpointUrl)},
                    AVAILABILITY_MODE = {SqlQuoting.ValidateAvailabilityMode(r.AvailabilityMode)},
                    FAILOVER_MODE = MANUAL,
                    SEEDING_MODE = {SqlQuoting.ValidateSeedingMode(r.SeedingMode)},
                    SECONDARY_ROLE (ALLOW_CONNECTIONS = {SqlQuoting.ValidateAllowConnections(r.AllowConnections)})
            """;

            if (r.BackupPriority.HasValue)
                clause += $",\n        BACKUP_PRIORITY = {r.BackupPriority.Value}";
            if (r.SessionTimeout.HasValue)
                clause += $",\n        SESSION_TIMEOUT = {r.SessionTimeout.Value}";

            clause += "\n    )";
            return clause;
        });

        var replicaSql = string.Join(",\n    ", replicaClauses);

        var backupPref = topology.Edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
            ? ", AUTOMATED_BACKUP_PREFERENCE = SECONDARY"
            : "";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(topology.AgName)}
            WITH (CLUSTER_TYPE = NONE{backupPref})
            FOR REPLICA ON
                {replicaSql}
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task JoinAvailabilityGroupAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} JOIN WITH (CLUSTER_TYPE = NONE)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantCreateAnyDatabaseAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} GRANT CREATE ANY DATABASE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropAvailabilityGroupAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Database management ───────────────────────────────

    public async Task<bool> DatabaseExistsOnPrimaryAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name AND state_desc = 'ONLINE'";
        cmd.Parameters.AddWithValue("@name", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    public async Task<string?> GetDatabaseAgNameAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ag.name
            FROM sys.availability_databases_cluster adc
            JOIN sys.availability_groups ag ON ag.group_id = adc.group_id
            WHERE adc.database_name = @name
            """;
        cmd.Parameters.AddWithValue("@name", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task PrepareDatabaseForAgAsync(SqlConnection connection, string database, string backupPath, CancellationToken ct = default)
    {
        // Set FULL recovery model
        await using var cmd1 = connection.CreateCommand();
        cmd1.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET RECOVERY FULL";
        await cmd1.ExecuteNonQueryAsync(ct);

        // Take initial backup to establish log chain.
        // CommandTimeout = 0 (unlimited): a full backup of a large database easily exceeds
        // the 30s default and a timeout here leaves the AG-database CR stuck in Failed.
        await using var cmd2 = connection.CreateCommand();
        cmd2.CommandTimeout = 0;
        cmd2.CommandText = $"BACKUP DATABASE {SqlQuoting.QuoteIdentifier(database)} TO DISK = @path WITH FORMAT, INIT, COMPRESSION";
        cmd2.Parameters.AddWithValue("@path", backupPath);
        await cmd2.ExecuteNonQueryAsync(ct);
    }

    public async Task AddDatabaseToAgAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} ADD DATABASE {SqlQuoting.QuoteIdentifier(database)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveDatabaseFromAgAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} REMOVE DATABASE {SqlQuoting.QuoteIdentifier(database)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SuspendDataMovementAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET HADR SUSPEND";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResumeDataMovementAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET HADR RESUME";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Replica management ────────────────────────────────

    public async Task AddReplicaAsync(SqlConnection connection, string agName, AgReplicaConfig replica, CancellationToken ct = default)
    {
        var sql = $"""
            ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}
            ADD REPLICA ON {SqlQuoting.QuoteNStringLiteral(replica.Name)} WITH (
                ENDPOINT_URL = {SqlQuoting.QuoteNStringLiteral(replica.EndpointUrl)},
                AVAILABILITY_MODE = {SqlQuoting.ValidateAvailabilityMode(replica.AvailabilityMode)},
                FAILOVER_MODE = MANUAL,
                SEEDING_MODE = {SqlQuoting.ValidateSeedingMode(replica.SeedingMode)},
                SECONDARY_ROLE (ALLOW_CONNECTIONS = {SqlQuoting.ValidateAllowConnections(replica.AllowConnections)})
            """;

        if (replica.BackupPriority.HasValue)
            sql += $",\n    BACKUP_PRIORITY = {replica.BackupPriority.Value}";
        if (replica.SessionTimeout.HasValue)
            sql += $",\n    SESSION_TIMEOUT = {replica.SessionTimeout.Value}";

        sql += "\n)";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveReplicaAsync(SqlConnection connection, string agName, string replicaName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} REMOVE REPLICA ON {SqlQuoting.QuoteNStringLiteral(replicaName)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetRequiredSyncSecondariesAsync(SqlConnection connection, string agName, int count, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} SET (REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT = {count})";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> GetRequiredSyncSecondariesAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT required_synchronized_secondaries_to_commit FROM sys.availability_groups WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", agName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    // ── Failover ──────────────────────────────────────────

    public async Task FailoverAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} FAILOVER";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ForceFailoverWithDataLossAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} FORCE_FAILOVER_ALLOW_DATA_LOSS";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Monitoring ────────────────────────────────────────

    public async Task<string?> GetLocalReplicaRoleAsync(SqlConnection connection, string agName, CancellationToken ct = default)
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

    public async Task<List<AgReplicaStateInfo>> GetReplicaStatesAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        var replicas = new List<AgReplicaStateInfo>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                ar.replica_server_name,
                ISNULL(ars.role_desc, 'UNKNOWN'),
                ISNULL(ars.synchronization_health_desc, 'UNKNOWN'),
                ISNULL(ars.connected_state_desc, 'UNKNOWN'),
                ISNULL(ars.operational_state_desc, 'UNKNOWN'),
                ISNULL(drs.is_failover_ready, 0),
                ISNULL(ar.secondary_role_allow_connections_desc, 'UNKNOWN'),
                ISNULL(ar.availability_mode_desc, 'UNKNOWN')
            FROM sys.availability_replicas ar
            JOIN sys.availability_groups ag ON ar.group_id = ag.group_id
            LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id
            LEFT JOIN (
                SELECT replica_id, MIN(CAST(is_failover_ready AS INT)) AS is_failover_ready
                FROM sys.dm_hadr_database_replica_states
                GROUP BY replica_id
            ) drs ON drs.replica_id = ar.replica_id
            WHERE ag.name = @name
            """;
        cmd.Parameters.AddWithValue("@name", agName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            replicas.Add(new AgReplicaStateInfo(
                Name: reader.GetString(0),
                Role: reader.GetString(1),
                SynchronizationHealth: reader.GetString(2),
                ConnectionState: reader.GetString(3),
                OperationalState: reader.GetString(4),
                IsFailoverReady: reader.GetInt32(5) == 1,
                AllowConnections: reader.GetString(6),
                AvailabilityMode: reader.GetString(7)
            ));
        }
        return replicas;
    }

    public async Task<List<AgDatabaseStateInfo>> GetDatabaseStatesAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        var databases = new List<AgDatabaseStateInfo>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                d.name,
                ar.replica_server_name,
                ISNULL(ars.role_desc, 'UNKNOWN'),
                ISNULL(drs.synchronization_state_desc, 'UNKNOWN'),
                ISNULL(drs.is_failover_ready, 0),
                ISNULL(drs.log_send_queue_size, 0),
                ISNULL(drs.redo_queue_size, 0)
            FROM sys.dm_hadr_database_replica_states drs
            JOIN sys.databases d ON drs.database_id = d.database_id
            JOIN sys.availability_groups ag ON drs.group_id = ag.group_id
            JOIN sys.availability_replicas ar ON drs.replica_id = ar.replica_id
            LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id
            WHERE ag.name = @name
            """;
        cmd.Parameters.AddWithValue("@name", agName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            databases.Add(new AgDatabaseStateInfo(
                DatabaseName: reader.GetString(0),
                ReplicaName: reader.GetString(1),
                Role: reader.GetString(2),
                SynchronizationState: reader.GetString(3),
                IsFailoverReady: reader.GetBoolean(4),
                LogSendQueueSize: reader.GetInt64(5),
                RedoQueueSize: reader.GetInt64(6)
            ));
        }
        return databases;
    }

    public async Task<bool> IsPrimaryAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        var role = await GetLocalReplicaRoleAsync(connection, agName, ct);
        return string.Equals(role, "PRIMARY", StringComparison.OrdinalIgnoreCase);
    }

    // ── Enterprise-only features ──────────────────────────

    public async Task ConfigureReadOnlyRoutingAsync(SqlConnection connection, string agName, string primaryName, List<string> secondaryNames, int listenerPort, string headlessService, string ns, CancellationToken ct = default)
    {
        // Set read-only routing URL for each secondary
        foreach (var secondary in secondaryNames)
        {
            var fqdn = $"{secondary}.{headlessService}.{ns}.svc.cluster.local";
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}
                MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(secondary)} WITH (
                    SECONDARY_ROLE (READ_ONLY_ROUTING_URL = {SqlQuoting.QuoteNStringLiteral($"tcp://{fqdn}:{listenerPort}")})
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Set read-only routing URL for primary (when it becomes secondary)
        {
            var primaryFqdn = $"{primaryName}.{headlessService}.{ns}.svc.cluster.local";
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}
                MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(primaryName)} WITH (
                    SECONDARY_ROLE (READ_ONLY_ROUTING_URL = {SqlQuoting.QuoteNStringLiteral($"tcp://{primaryFqdn}:{listenerPort}")})
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Set read-only routing list for primary
        {
            var routingList = string.Join(", ", secondaryNames.Select(SqlQuoting.QuoteNStringLiteral));
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}
                MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(primaryName)} WITH (
                    PRIMARY_ROLE (READ_ONLY_ROUTING_LIST = ({routingList}))
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SetBackupPreferenceAsync(SqlConnection connection, string agName, string preference, CancellationToken ct = default)
    {
        // preference: PRIMARY, SECONDARY, SECONDARY_ONLY, NONE
        var sqlPref = preference.ToUpperInvariant() switch
        {
            "PRIMARY" => "PRIMARY",
            "SECONDARY" => "SECONDARY",
            "PREFER_PRIMARY" => "PRIMARY",
            "NONE" => "NONE",
            _ => "SECONDARY"
        };

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} SET (AUTOMATED_BACKUP_PREFERENCE = {sqlPref})";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
