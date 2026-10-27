namespace MsSqlOperator.Services.SqlServer;

// ── Support types ─────────────────────────────────────────

public record AgTopologyConfig(
    string AgName,
    string HeadlessServiceName,
    string Namespace,
    int EndpointPort,
    string Edition,
    List<AgReplicaConfig> Replicas,
    string? BackupPreference = null  // spec value (Primary|Secondary|SecondaryOnly|None), Enterprise only
);

public record AgReplicaConfig(
    string Name,
    string EndpointUrl,
    string AvailabilityMode,        // SYNCHRONOUS_COMMIT | ASYNCHRONOUS_COMMIT
    string FailoverMode,            // MANUAL (always for CLUSTER_TYPE = NONE)
    string SeedingMode,             // AUTOMATIC | MANUAL
    string AllowConnections,        // NO | READ_ONLY | ALL
    int? BackupPriority,            // 0-100, Enterprise only
    int? SessionTimeout             // seconds
);

public record AgReplicaStateInfo(
    string Name,
    string Role,
    string SynchronizationHealth,
    string ConnectionState,
    string OperationalState,
    bool IsFailoverReady,
    string AllowConnections = "UNKNOWN",   // secondary_role_allow_connections_desc: NO | READ_ONLY | ALL
    string AvailabilityMode = "UNKNOWN"    // availability_mode_desc: SYNCHRONOUS_COMMIT | ASYNCHRONOUS_COMMIT
);

/// <summary>Percent of the transaction log in use and the reason it cannot be truncated.</summary>
public record LogUsageInfo(int PercentUsed, string ReuseWait);

public record AgDatabaseStateInfo(
    string DatabaseName,
    string ReplicaName,
    string Role,
    string SynchronizationState,
    bool IsFailoverReady,
    long LogSendQueueSize,
    long RedoQueueSize,
    // Suspension is not a synchronization_state_desc value (a suspended database reports
    // NOT SYNCHRONIZING there): it comes from drs.is_suspended / suspend_reason_desc.
    bool IsSuspended = false,
    string? SuspendReason = null,
    // drs.last_hardened_lsn, as seen by the replica that answered.
    decimal? LastHardenedLsn = null
);

/// <summary>Automatic-seeding progress for one database on one AG.</summary>
public record AgSeedingStateInfo(
    string CurrentState,     // COMPLETED | FAILED | IN_PROGRESS | UNKNOWN
    string FailureState,     // failure_state_desc, empty when none
    bool PerformedSeeding
);

// ── Interface ─────────────────────────────────────────────

public interface ISqlAgManager
{
    // ── Infrastructure cryptographique ────────────────────
    Task<bool> MasterKeyExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task CreateMasterKeyAsync(Microsoft.Data.SqlClient.SqlConnection connection, string password, CancellationToken ct = default);
    /// <summary>Creates the replica's HADR certificate; <paramref name="expiryDate"/> (a date literal
    /// accepted by <see cref="SqlQuoting.ValidateDateLiteral"/>) overrides SQL Server's one-year default.</summary>
    Task CreateHadrCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string subject, string? expiryDate = null, CancellationToken ct = default);
    Task<byte[]> ExportHadrPublicCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);
    Task ImportTrustedReplicaCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, byte[] publicCertBytes, CancellationToken ct = default);
    Task<bool> CertificateExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);
    Task<string?> GetCertificateThumbprintAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);

    // ── Logins ────────────────────────────────────────────
    Task<bool> LoginExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, CancellationToken ct = default);
    Task CreateEndpointLoginFromCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string certName, CancellationToken ct = default);

    // ── Endpoint HADR ─────────────────────────────────────
    Task<bool> EndpointExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName, CancellationToken ct = default);
    Task CreateEndpointAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName, int port, string certName, string algorithm, CancellationToken ct = default);
    Task GrantEndpointConnectAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName, string login, CancellationToken ct = default);

    /// <summary>Drops the endpoint login and the trusted certificate of another replica, when they
    /// exist — that replica lost its private key and comes back with a new certificate.</summary>
    Task RemoveTrustedReplicaAsync(Microsoft.Data.SqlClient.SqlConnection connection, string loginName, string certName, CancellationToken ct = default);

    // ── Availability Group lifecycle ──────────────────────
    Task<bool> AvailabilityGroupExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task CreateAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, AgTopologyConfig topology, CancellationToken ct = default);
    Task JoinAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task GrantCreateAnyDatabaseAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Database management ───────────────────────────────
    Task<bool> DatabaseExistsOnPrimaryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task<string?> GetDatabaseAgNameAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    /// <summary>Establishes the log chain (FULL recovery, then a full or differential backup and a log
    /// backup when the chain is missing). Returns true when backups were taken.</summary>
    Task<bool> PrepareDatabaseForAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string backupPath, CancellationToken ct = default);
    Task<string?> GetRecoveryModelAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task<(long DatabaseSizeMb, long FreeSpaceMb)?> GetBackupSpaceEstimateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string backupPath, CancellationToken ct = default);
    Task<AgSeedingStateInfo?> GetSeedingStateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task AddDatabaseToAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task RemoveDatabaseFromAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task SuspendDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task ResumeDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);

    // ── Replica management ────────────────────────────────
    Task SetRequiredSyncSecondariesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, int count, CancellationToken ct = default);
    Task<int?> GetRequiredSyncSecondariesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    /// <summary>sys.availability_groups.sequence_number seen by this replica: equal on the primary
    /// and the target is a documented precondition of a planned clusterless failover.</summary>
    Task<long?> GetAgSequenceNumberAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Failover ──────────────────────────────────────────
    Task FailoverAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task ForceFailoverWithDataLossAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    /// <summary>ALTER AVAILABILITY GROUP … OFFLINE — takes a reachable primary out of the writer role
    /// (RESOLVING) before the target is promoted.</summary>
    Task DemoteToSecondaryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    /// <summary>ALTER AVAILABILITY GROUP … SET (ROLE = SECONDARY) on a replica that is no longer
    /// online as primary (RESOLVING): the last step of a clusterless failover, once the target is
    /// promoted.</summary>
    Task SetSecondaryRoleAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    /// <summary>Makes the private key of the HADR endpoint certificate usable again when the Service
    /// Master Key could not be decrypted (a new machine key): re-binds the database master key to the
    /// service master key with its password, regenerating the latter when needed, then restarts the
    /// endpoint. Returns what was done, or null when the key was usable.</summary>
    Task<string?> EnsureEndpointKeyUsableAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName,
        string masterKeyPassword, CancellationToken ct = default);

    /// <summary>ALTER ENDPOINT … STATE = STOPPED then STARTED: drops every HADR connection of this
    /// replica and dials its partners again.</summary>
    Task RestartEndpointAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName, CancellationToken ct = default);

    // ── Monitoring ────────────────────────────────────────
    Task<string?> GetLocalReplicaRoleAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    /// <summary>SERVERPROPERTY('ProductVersion') of this replica.</summary>
    Task<string?> GetProductVersionAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<List<AgReplicaStateInfo>> GetReplicaStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<List<AgDatabaseStateInfo>> GetDatabaseStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    /// <summary>Transaction log usage of a database on this replica and what holds its truncation
    /// (log_reuse_wait_desc). Null when the database is not there.</summary>
    Task<LogUsageInfo?> GetLogUsageAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task<bool> IsPrimaryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Enterprise-only features ──────────────────────────
    Task ConfigureReadOnlyRoutingAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string primaryName, List<string> secondaryNames, string headlessService, string ns, CancellationToken ct = default);
    Task SetBackupPreferenceAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string preference, CancellationToken ct = default);
    /// <summary>automated_backup_preference_desc as SQL Server reports it (PRIMARY, SECONDARY_ONLY…).</summary>
    Task<string?> GetBackupPreferenceAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
}
