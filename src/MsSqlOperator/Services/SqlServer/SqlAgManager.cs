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
        cmd.CommandText = $"CREATE MASTER KEY ENCRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(password)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateHadrCertificateAsync(SqlConnection connection, string certName, string subject, string? expiryDate = null, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // SUBJECT and EXPIRY_DATE in CREATE CERTIFICATE must be literals, not parameters.
        var expiryClause = expiryDate is null ? "" : $", EXPIRY_DATE = {SqlQuoting.QuoteStringLiteral(SqlQuoting.ValidateDateLiteral(expiryDate))}";
        cmd.CommandText = $"CREATE CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)} WITH SUBJECT = {SqlQuoting.QuoteNStringLiteral(subject)}{expiryClause}";
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

    public async Task RemoveTrustedReplicaAsync(SqlConnection connection, string loginName, string certName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"IF SUSER_ID(@login) IS NOT NULL DROP LOGIN {SqlQuoting.QuoteIdentifier(loginName)}; " +
            $"IF CERT_ID(@cert) IS NOT NULL DROP CERTIFICATE {SqlQuoting.QuoteIdentifier(certName)};";
        cmd.Parameters.AddWithValue("@login", loginName);
        cmd.Parameters.AddWithValue("@cert", certName);
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

    /// <summary>The definition of one replica, as CREATE and ADD REPLICA take it.</summary>
    private static string ReplicaClause(AgReplicaConfig r)
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

        return clause + "\n    )";
    }

    public async Task CreateAvailabilityGroupAsync(SqlConnection connection, AgTopologyConfig topology, CancellationToken ct = default)
    {
        var replicaSql = string.Join(",\n    ", topology.Replicas.Select(ReplicaClause));

        // The preference of the spec from the start (it used to be forced to SECONDARY here).
        var backupPref = topology.Edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase)
            ? $", AUTOMATED_BACKUP_PREFERENCE = {ToSqlBackupPreference(topology.BackupPreference ?? "Secondary")}"
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

    public async Task RemoveReplicaAsync(SqlConnection connection, string agName, string replica, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} REMOVE REPLICA ON {SqlQuoting.QuoteNStringLiteral(replica)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddReplicaAsync(SqlConnection connection, string agName, AgReplicaConfig replica, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} ADD REPLICA ON {ReplicaClause(replica)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropLocalAvailabilityGroupAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.availability_groups WHERE name = @name) DROP AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}";
        cmd.Parameters.AddWithValue("@name", agName);
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<string>> GetAgDatabaseNamesAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT adc.database_name
            FROM sys.availability_databases_cluster adc
            JOIN sys.availability_groups ag ON ag.group_id = adc.group_id
            WHERE ag.name = @name
            """;
        cmd.Parameters.AddWithValue("@name", agName);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<List<HeldLogInfo>> GetLogsHeldByReplicasAsync(SqlConnection connection, CancellationToken ct = default)
    {
        // dm_db_log_space_usage only reports the current database; the counter covers them all.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.name, CAST(pc.cntr_value AS int)
            FROM sys.databases d
            JOIN sys.dm_os_performance_counters pc
              ON RTRIM(pc.instance_name) = d.name
             AND RTRIM(pc.counter_name) = 'Percent Log Used'
             AND pc.object_name LIKE '%:Databases%'
            WHERE d.replica_id IS NOT NULL AND d.log_reuse_wait_desc = 'AVAILABILITY_REPLICA'
            """;
        var held = new List<HeldLogInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            held.Add(new HeldLogInfo(reader.GetString(0), reader.GetInt32(1)));
        return held;
    }

    public async Task GrantCreateAnyDatabaseAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} GRANT CREATE ANY DATABASE";
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

    /// <summary>
    /// Establishes the log chain a database needs before ADD DATABASE.
    /// The engine's own signal is used, not an msdb heuristic: <c>last_log_backup_lsn</c> is
    /// non-NULL exactly when a log chain is established under FULL recovery, which is the real
    /// ADD DATABASE prerequisite. The previous check ("any type='D' backupset in the last 24h")
    /// accepted fulls taken under SIMPLE, copy-only fulls of a same-named dropped database, and
    /// externally-taken fulls — then ADD DATABASE failed in a loop for 24h.
    ///
    /// The backup used to be COPY_ONLY, which does NOT start a log chain —
    /// last_log_backup_lsn stayed NULL, so every pass took another full backup of the primary and
    /// ADD DATABASE failed with 1475 (changes minimally logged while the database had no chain).
    /// Without a chain there is nothing to break, except the differential base of a backup plan
    /// that ran before a SIMPLE→FULL switch: a differential backup then starts the chain and keeps
    /// that base; otherwise a full backup does. A log backup follows, which is what ADD DATABASE
    /// requires after minimally logged changes — the two files open the log chain.
    /// Returns true when backups were actually taken.
    /// </summary>
    public async Task<List<BackupChainEntry>> GetBackupChainAsync(SqlConnection connection, string database, int limit, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@limit) bs.type, bs.backup_finish_date,
                   CONVERT(varchar(30), bs.first_lsn), CONVERT(varchar(30), bs.last_lsn), bmf.physical_device_name
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
            WHERE bs.database_name = @db AND bs.type IN ('D', 'I', 'L') AND bs.is_copy_only = 0
            ORDER BY bs.backup_finish_date DESC
            """;
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@limit", limit);
        var entries = new List<BackupChainEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(new BackupChainEntry(reader.GetString(0), reader.GetDateTime(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4)));
        return entries;
    }

    public async Task SetSynchronousCommitAsync(SqlConnection primaryConnection, string agName, string replica, CancellationToken ct = default)
    {
        await using var cmd = primaryConnection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(replica)} " +
                          "WITH (AVAILABILITY_MODE = SYNCHRONOUS_COMMIT)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<double?> GetBackupProgressAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (1) CAST(percent_complete AS float) FROM sys.dm_exec_requests
            WHERE command LIKE N'BACKUP%' AND database_id = DB_ID(@db)
            """;
        cmd.Parameters.AddWithValue("@db", database);
        return await cmd.ExecuteScalarAsync(ct) is double percent ? percent : null;
    }

    public async Task<bool> HasLogChainAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        // The log chain is established (and survives the SIMPLE→FULL switch) exactly when the
        // engine reports a last log backup LSN for the database.
        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*)
            FROM sys.database_recovery_status drs
            JOIN sys.databases d ON d.database_id = drs.database_id
            WHERE d.name = @db AND d.recovery_model_desc = 'FULL' AND drs.last_log_backup_lsn IS NOT NULL
            """;
        check.Parameters.AddWithValue("@db", database);
        return Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<bool> PrepareDatabaseForAgAsync(SqlConnection connection, string database, string backupPath, CancellationToken ct = default)
    {
        // Set FULL recovery model
        await using var cmd1 = connection.CreateCommand();
        cmd1.CommandText = $"ALTER DATABASE {SqlQuoting.QuoteIdentifier(database)} SET RECOVERY FULL";
        await cmd1.ExecuteNonQueryAsync(ct);

        if (await HasLogChainAsync(connection, database, ct))
            return false;

        bool hasDifferentialBase;
        await using (var baseCheck = connection.CreateCommand())
        {
            baseCheck.CommandText = """
                SELECT COUNT(*) FROM sys.master_files
                WHERE database_id = DB_ID(@db) AND type = 0 AND differential_base_lsn IS NOT NULL
                """;
            baseCheck.Parameters.AddWithValue("@db", database);
            hasDifferentialBase = Convert.ToInt32(await baseCheck.ExecuteScalarAsync(ct)) > 0;
        }

        // CommandTimeout = 0 (unlimited): a full backup of a large database easily exceeds
        // the 30s default and a timeout here leaves the AG-database CR stuck in Failed.
        var db = SqlQuoting.QuoteIdentifier(database);
        await using var full = connection.CreateCommand();
        full.CommandTimeout = 0;
        full.CommandText = hasDifferentialBase
            ? $"BACKUP DATABASE {db} TO DISK = @path WITH DIFFERENTIAL, FORMAT, INIT, COMPRESSION"
            : $"BACKUP DATABASE {db} TO DISK = @path WITH FORMAT, INIT, COMPRESSION";
        full.Parameters.AddWithValue("@path", backupPath);
        await full.ExecuteNonQueryAsync(ct);

        await using var log = connection.CreateCommand();
        log.CommandTimeout = 0;
        log.CommandText = $"BACKUP LOG {db} TO DISK = @path WITH FORMAT, INIT, COMPRESSION";
        log.Parameters.AddWithValue("@path", Path.ChangeExtension(backupPath, ".trn"));
        await log.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Recovery model of a database, e.g. FULL / SIMPLE / BULK_LOGGED:
    /// converting SIMPLE → FULL silently starts an unbounded log growth unless log backups exist,
    /// so the caller must be able to warn about the conversion it is about to perform.</summary>
    public async Task<string?> GetRecoveryModelAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT recovery_model_desc FROM sys.databases WHERE name = @db";
        cmd.Parameters.AddWithValue("@db", database);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>Free space (MB) on the volume backing a database's files — used to fail fast
    /// instead of running a multi-hour BACKUP into a full PVC.</summary>
    public async Task<(long DatabaseSizeMb, long FreeSpaceMb)?> GetBackupSpaceEstimateAsync(
        SqlConnection connection, string database, string backupPath, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // available_bytes of the volume hosting the backup directory is not directly queryable;
        // the volume of the database's own data files is a good proxy only when they share a mount,
        // so ask for both the database size and the free space of the *backup* volume via the
        // dm_os_volume_stats of any file that lives on the same mount point prefix.
        cmd.CommandText = """
            SELECT
                CAST(SUM(CAST(mf.size AS BIGINT)) * 8 / 1024 AS BIGINT) AS db_size_mb,
                CAST(MIN(vs.available_bytes) / 1048576 AS BIGINT) AS free_mb
            FROM sys.master_files mf
            CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
            WHERE mf.database_id = DB_ID(@db)
            """;
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || await reader.IsDBNullAsync(0, ct) || await reader.IsDBNullAsync(1, ct))
            return null;
        return (reader.GetInt64(0), reader.GetInt64(1));
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

    public async Task<long?> GetAgSequenceNumberAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sequence_number FROM sys.availability_groups WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", agName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    // ── Failover ──────────────────────────────────────────

    /// <summary>
    /// Planned (graceful) failover. AGs created with CLUSTER_TYPE = NONE — the only option on
    /// Linux without WSFC/Pacemaker, and what this operator always uses — do NOT support
    /// <c>ALTER AVAILABILITY GROUP ... FAILOVER</c> (SQL error 47122); the only supported manual
    /// failover is FORCE_FAILOVER_ALLOW_DATA_LOSS. It is nonetheless lossless here because the
    /// orchestrator only reaches this path after confirming the target is a SYNCHRONOUS_COMMIT
    /// replica in the SYNCHRONIZED / failover-ready state (is_failover_ready = 1): a synchronized
    /// secondary holds every committed transaction, so RPO = 0. The "data loss" wording is the
    /// required clusterless syntax, not an outcome. <see cref="ForceFailoverWithDataLossAsync"/>
    /// issues the same command but is reached on the orchestrator's *unsynchronized* path, where
    /// loss is genuinely possible.
    /// </summary>
    public async Task FailoverAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} FORCE_FAILOVER_ALLOW_DATA_LOSS";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ForceFailoverWithDataLossAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} FORCE_FAILOVER_ALLOW_DATA_LOSS";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Takes a reachable primary out of the writer role. This is the missing half of the
    /// clusterless failover protocol: <c>FORCE_FAILOVER_ALLOW_DATA_LOSS</c> promotes the target but
    /// never demotes the source, so a source that is still alive keeps accepting writes through
    /// established connections and direct pod FQDNs — two primaries, divergent writes. The old
    /// primary goes OFFLINE first, the target is promoted, then the old primary takes the secondary
    /// role; only when it cannot be reached does isolation fall back to fencing.
    /// </summary>
    public async Task DemoteToSecondaryAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        // SET (ROLE = SECONDARY) on a primary that is still online fails with 41104 ("the
        // availability group resource did not come online") on SQL Server 2022 CU25, every time. The
        // primary is taken OFFLINE instead: it goes RESOLVING and accepts no more writes; it becomes a
        // secondary (SetSecondaryRoleAsync) once the target is promoted.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} OFFLINE";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSecondaryRoleAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} SET (ROLE = SECONDARY)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A pod recreated with another machine key cannot decrypt its Service Master Key, so
    /// the database master key no longer opens automatically and the private key of the endpoint
    /// certificate is unusable — every HADR handshake fails with 15581 and the replica cannot rejoin.
    /// The master key is password-protected too (masterKeyPasswordSecretRef): it is opened with that
    /// password and bound to the service master key again. The endpoint is restarted so it loads the
    /// key. The service master key is never regenerated — what it protects would be lost for
    /// good; a key that stays unusable is a human's call (<see cref="EndpointKeyUnusableException"/>).
    /// </summary>
    public async Task<string?> EnsureEndpointKeyUsableAsync(SqlConnection connection, string endpointName,
        string masterKeyPassword, CancellationToken ct = default)
    {
        if (await EndpointKeyUsableAsync(connection, endpointName, ct))
            return null;

        var rebind = $"OPEN MASTER KEY DECRYPTION BY PASSWORD = {SqlQuoting.QuoteNStringLiteral(masterKeyPassword)}; " +
                     "ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY; CLOSE MASTER KEY;";
        try
        {
            await ExecAsync(connection, rebind, ct);
        }
        catch (SqlException ex)
        {
            var reason = EndpointKeyUnusableException.Classify(ex.Errors.Cast<SqlError>().Select(e => e.Number));
            throw new EndpointKeyUnusableException(reason,
                $"The database master key could not be re-bound to the service master key ({ex.Message}).", ex);
        }
        if (!await EndpointKeyUsableAsync(connection, endpointName, ct))
            throw new EndpointKeyUnusableException(EndpointKeyUnusableException.Unclassified,
                $"The private key of the certificate of endpoint {endpointName} is still unusable after the database master key was re-bound.");

        await RestartEndpointAsync(connection, endpointName, ct);
        return "database master key re-bound to the service master key";
    }

    public async Task RestartEndpointAsync(SqlConnection connection, string endpointName, CancellationToken ct = default)
    {
        var endpoint = SqlQuoting.QuoteIdentifier(endpointName);
        await ExecAsync(connection, $"ALTER ENDPOINT {endpoint} STATE = STOPPED; ALTER ENDPOINT {endpoint} STATE = STARTED;", ct);
    }

    private static async Task<bool> EndpointKeyUsableAsync(SqlConnection connection, string endpointName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM master.sys.database_mirroring_endpoints e
            JOIN master.sys.certificates c ON c.certificate_id = e.certificate_id
            WHERE e.name = @ep AND SIGNBYCERT(c.certificate_id, N'probe') IS NULL
            """;
        cmd.Parameters.AddWithValue("@ep", endpointName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 0;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Engine build of this replica — e.g. "16.0.4125.3". Failover
    /// target selection must never promote a replica running an older major version than the last
    /// known primary: the databases would be upgraded to a format the others cannot redo.</summary>
    public async Task<string?> GetProductVersionAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductVersion'))";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>
    /// Automatic-seeding progress for a database. <c>current_state</c> is one of
    /// COMPLETED / FAILED / IN_PROGRESS; <c>failure_state_desc</c> carries the cause. Without this
    /// the operator polls a database that will never converge and re-issues RESUME forever.
    ///
    /// The query ordered by <c>start_time_utc</c>, a column the DMV does not have
    /// (<c>start_time</c>): it failed on every pass and the database never left Seeding. It also
    /// read the single latest operation, which hid the failed seeding of another secondary: the
    /// latest operation of EACH secondary is read now, the worst of them reported.
    /// </summary>
    public async Task<AgSeedingStateInfo?> GetSeedingStateAsync(SqlConnection connection, string agName, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH latest AS (
                SELECT ahs.current_state, ahs.failure_state_desc, ahs.performed_seeding,
                       ROW_NUMBER() OVER (PARTITION BY ahs.ag_remote_replica_id ORDER BY ahs.start_time DESC) AS rn
                FROM sys.dm_hadr_automatic_seeding ahs
                JOIN sys.availability_groups ag ON ag.group_id = ahs.ag_id
                JOIN sys.availability_databases_cluster adc
                    ON adc.group_id = ahs.ag_id AND adc.group_database_id = ahs.ag_db_id
                WHERE ag.name = @ag AND adc.database_name = @db)
            SELECT TOP 1
                ISNULL(current_state, 'UNKNOWN'),
                ISNULL(failure_state_desc, ''),
                ISNULL(performed_seeding, 0)
            FROM latest
            WHERE rn = 1
            ORDER BY CASE current_state WHEN 'FAILED' THEN 0 WHEN 'IN_PROGRESS' THEN 1 ELSE 2 END
            """;
        cmd.Parameters.AddWithValue("@ag", agName);
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new AgSeedingStateInfo(
            CurrentState: reader.GetString(0),
            FailureState: reader.GetString(1),
            PerformedSeeding: reader.GetBoolean(2));
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

    public async Task<List<FileVolumeInfo>> GetFileVolumesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        // volume_mount_point is NULL on Linux: the caller maps each file to its volume by path.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT mf.physical_name, vs.total_bytes, vs.available_bytes
            FROM sys.master_files mf
            CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
            """;
        var files = new List<FileVolumeInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            files.Add(new FileVolumeInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return files;
    }

    public async Task<TimeSpan> GetInstanceUptimeAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DATEDIFF_BIG(MILLISECOND, sqlserver_start_time, SYSDATETIME()) FROM sys.dm_os_sys_info";
        return TimeSpan.FromMilliseconds(Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture));
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
                -- is_failover_ready lives in dm_hadr_database_replica_CLUSTER_states, NOT
                -- dm_hadr_database_replica_states (the latter has no such column → Msg 207).
                SELECT replica_id, MIN(CAST(is_failover_ready AS INT)) AS is_failover_ready
                FROM sys.dm_hadr_database_replica_cluster_states
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

    public async Task DropSecondaryCopyAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        var db = SqlQuoting.QuoteIdentifier(database);
        await using var cmd = connection.CreateCommand();
        // The guard runs in the same batch as the drop. A copy online and writable outside any
        // availability group may hold data of its own (taken out by hand, restored WITH RECOVERY), and
        // the primary copy is the data itself: both are refused.
        cmd.CommandText =
            "IF EXISTS (SELECT 1 FROM sys.databases d WHERE d.name = @db AND d.state_desc = N'ONLINE' AND d.is_read_only = 0 " +
            "AND NOT EXISTS (SELECT 1 FROM sys.dm_hadr_database_replica_states s WHERE s.database_id = d.database_id AND s.is_local = 1)) " +
            $"THROW {SecondaryCopyNotDroppedException.SqlError}, N'mssqlop: the copy is online and writable outside any availability group', 1; " +
            "IF EXISTS (SELECT 1 FROM sys.dm_hadr_database_replica_states WHERE database_id = DB_ID(@db) AND is_local = 1 AND is_primary_replica = 1) " +
            $"THROW {SecondaryCopyNotDroppedException.SqlError}, N'mssqlop: the copy is the primary copy', 1; " +
            $"IF EXISTS (SELECT 1 FROM sys.dm_hadr_database_replica_states WHERE database_id = DB_ID(@db) AND is_local = 1) ALTER DATABASE {db} SET HADR OFF; " +
            $"IF DB_ID(@db) IS NOT NULL DROP DATABASE {db};";
        cmd.Parameters.AddWithValue("@db", database);
        // SET HADR OFF waits for the redo of the copy to stop.
        cmd.CommandTimeout = 120;
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Errors.Cast<SqlError>().Any(e => e.Number == SecondaryCopyNotDroppedException.SqlError))
        {
            throw new SecondaryCopyNotDroppedException(database, ex);
        }
    }

    public async Task<List<string>> GetUserDatabaseNamesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task RequestSeedingAsync(SqlConnection primaryConnection, string agName, string replica, CancellationToken ct = default)
    {
        await using var cmd = primaryConnection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(replica)} " +
                          "WITH (SEEDING_MODE = AUTOMATIC)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReplicaSeedingInfo?> GetReplicaSeedingAsync(SqlConnection primaryConnection, string agName, string database,
        string replica, CancellationToken ct = default)
    {
        await using var cmd = primaryConnection.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 ahs.start_time, ISNULL(ahs.current_state, 'UNKNOWN'), ISNULL(ahs.failure_state_desc, '')
            FROM sys.dm_hadr_automatic_seeding ahs
            JOIN sys.availability_groups ag ON ag.group_id = ahs.ag_id
            JOIN sys.availability_databases_cluster adc ON adc.group_id = ahs.ag_id AND adc.group_database_id = ahs.ag_db_id
            JOIN sys.availability_replicas ar ON ar.replica_id = ahs.ag_remote_replica_id
            WHERE ag.name = @ag AND adc.database_name = @db AND ar.replica_server_name = @replica
            ORDER BY ahs.start_time DESC
            """;
        cmd.Parameters.AddWithValue("@ag", agName);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@replica", replica);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ReplicaSeedingInfo(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<List<DatabaseFileSpace>> GetDatabaseFileSpaceAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.type, SUM(CAST(f.size AS bigint)) * 8192, MIN(vs.available_bytes)
            FROM sys.master_files f
            CROSS APPLY sys.dm_os_volume_stats(f.database_id, f.file_id) vs
            WHERE f.database_id = DB_ID(@db) AND f.type IN (0, 1)
            GROUP BY f.type
            """;
        cmd.Parameters.AddWithValue("@db", database);
        var files = new List<DatabaseFileSpace>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            files.Add(new DatabaseFileSpace(reader.GetByte(0), reader.GetInt64(1), reader.GetInt64(2)));
        return files;
    }

    public async Task<string> BackupLogAsync(SqlConnection connection, string database, string folder, DateTime utcNow, CancellationToken ct = default)
    {
        var file = $"{folder.TrimEnd('/')}/{database}_{utcNow:yyyyMMdd'T'HHmmssfff}Z.trn";
        await using var cmd = connection.CreateCommand();
        // Uncompressed, a day of these filled the 10 GB backup volume of the test AG.
        cmd.CommandText = $"EXEC master.dbo.xp_create_subdir @folder; BACKUP LOG {SqlQuoting.QuoteIdentifier(database)} TO DISK = @file WITH COMPRESSION;";
        cmd.Parameters.AddWithValue("@folder", folder);
        cmd.Parameters.AddWithValue("@file", file);
        // Within the reconcile budget: a log too large to back up in time is reported, not hung on.
        cmd.CommandTimeout = 100;
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Errors.Cast<SqlError>().Any(e => e.Number == BackupWriteFailedException.SqlError))
        {
            throw new BackupWriteFailedException(ex.Message, ex);
        }
        return file;
    }

    public async Task DeleteOldBackupsAsync(SqlConnection connection, string folder, string extension, DateTime cutoff, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // xp_delete_file reads each file's header: only SQL Server backup files are deleted.
        cmd.CommandText = "EXEC master.dbo.xp_delete_file 0, @folder, @extension, @cutoff, 0";
        cmd.Parameters.AddWithValue("@folder", folder.TrimEnd('/') + "/");
        cmd.Parameters.AddWithValue("@extension", extension);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LogUsageInfo?> GetLogUsageAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        // sys.dm_db_log_space_usage only describes the current database; the performance counter
        // answers for any database from master.
        cmd.CommandText = """
            SELECT CAST(ISNULL(pc.cntr_value, 0) AS int), d.log_reuse_wait_desc
            FROM sys.databases d
            LEFT JOIN sys.dm_os_performance_counters pc
                ON pc.counter_name LIKE 'Percent Log Used%' AND pc.instance_name = d.name
            WHERE d.name = @db
            """;
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new LogUsageInfo(reader.GetInt32(0), reader.GetString(1)) : null;
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
                ISNULL(dcs.is_failover_ready, 0),
                ISNULL(drs.log_send_queue_size, 0),
                ISNULL(drs.redo_queue_size, 0),
                ISNULL(drs.is_suspended, 0),
                drs.suspend_reason_desc,
                drs.last_hardened_lsn
            FROM sys.dm_hadr_database_replica_states drs
            JOIN sys.databases d ON drs.database_id = d.database_id
            JOIN sys.availability_groups ag ON drs.group_id = ag.group_id
            JOIN sys.availability_replicas ar ON drs.replica_id = ar.replica_id
            LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id
            -- is_failover_ready is per-database in dm_hadr_database_replica_CLUSTER_states.
            LEFT JOIN sys.dm_hadr_database_replica_cluster_states dcs
                ON dcs.replica_id = drs.replica_id AND dcs.group_database_id = drs.group_database_id
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
                RedoQueueSize: reader.GetInt64(6),
                IsSuspended: reader.GetBoolean(7),
                SuspendReason: reader.IsDBNull(8) ? null : reader.GetString(8),
                LastHardenedLsn: reader.IsDBNull(9) ? null : reader.GetDecimal(9)
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

    public async Task ConfigureReadOnlyRoutingAsync(SqlConnection connection, string agName, string primaryName, List<string> secondaryNames, string headlessService, string ns, CancellationToken ct = default,
        bool endWithPrimary = false)
    {
        // Routing URLs point at POD FQDNs, so they must carry the container port
        // (1433) — never the listener Service port. With a customized listener port the engine
        // handed clients a tcp://pod:<servicePort> URL nothing listens on and read-only routing
        // silently fell back to the primary.
        var port = Kubernetes.SqlEndpoints.ContainerPort;

        // Set read-only routing URL for each secondary
        foreach (var secondary in secondaryNames)
        {
            var fqdn = $"{secondary}.{headlessService}.{ns}.svc.cluster.local";
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)}
                MODIFY REPLICA ON {SqlQuoting.QuoteNStringLiteral(secondary)} WITH (
                    SECONDARY_ROLE (READ_ONLY_ROUTING_URL = {SqlQuoting.QuoteNStringLiteral($"tcp://{fqdn}:{port}")})
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
                    SECONDARY_ROLE (READ_ONLY_ROUTING_URL = {SqlQuoting.QuoteNStringLiteral($"tcp://{primaryFqdn}:{port}")})
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Set read-only routing list for primary
        {
            var routingList = string.Join(", ", (endWithPrimary ? [.. secondaryNames, primaryName] : secondaryNames).Select(SqlQuoting.QuoteNStringLiteral));
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

    /// <summary>
    /// The SQL keyword for a spec backupPreference: SecondaryOnly used to fall through to
    /// SECONDARY — backups then ran on the primary whenever no secondary was available, exactly
    /// what SecondaryOnly forbids.
    /// </summary>
    public static string ToSqlBackupPreference(string preference) => preference.ToUpperInvariant() switch
    {
        "PRIMARY" => "PRIMARY",
        "SECONDARY" => "SECONDARY",
        "SECONDARYONLY" or "SECONDARY_ONLY" => "SECONDARY_ONLY",
        "NONE" => "NONE",
        _ => throw new ArgumentException($"Unknown backup preference \"{preference}\" (expected Primary, Secondary, SecondaryOnly or None)."),
    };

    public async Task SetBackupPreferenceAsync(SqlConnection connection, string agName, string preference, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER AVAILABILITY GROUP {SqlQuoting.QuoteIdentifier(agName)} SET (AUTOMATED_BACKUP_PREFERENCE = {ToSqlBackupPreference(preference)})";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetBackupPreferenceAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT automated_backup_preference_desc FROM sys.availability_groups WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", agName);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task<string?> ReadDatabasePropertyAsync(SqlConnection connection, string database, string name, CancellationToken ct = default)
        => DatabaseProperties.ReadAsync(connection, database, name, ct);

    public Task WriteDatabasePropertyAsync(SqlConnection connection, string database, string name, string? value, CancellationToken ct = default)
        => DatabaseProperties.WriteAsync(connection, database, name, value, ct);
}
