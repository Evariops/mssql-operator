namespace MsSqlOperator.Services.SqlServer;

// ── Support types ─────────────────────────────────────────

public record AgTopologyConfig(
    string AgName,
    string HeadlessServiceName,
    string Namespace,
    int EndpointPort,
    string Edition,
    List<AgReplicaConfig> Replicas
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

public record AgDatabaseStateInfo(
    string DatabaseName,
    string ReplicaName,
    string Role,
    string SynchronizationState,
    bool IsFailoverReady,
    long LogSendQueueSize,
    long RedoQueueSize
);

// ── Interface ─────────────────────────────────────────────

public interface ISqlAgManager
{
    // ── Infrastructure cryptographique ────────────────────
    Task<bool> MasterKeyExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task CreateMasterKeyAsync(Microsoft.Data.SqlClient.SqlConnection connection, string password, CancellationToken ct = default);
    Task CreateHadrCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string subject, CancellationToken ct = default);
    Task ImportHadrCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string certPath, string keyPath, string decryptionPassword, CancellationToken ct = default);
    Task ExportHadrCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string certPath, string keyPath, string encryptionPassword, CancellationToken ct = default);
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

    // ── Availability Group lifecycle ──────────────────────
    Task<bool> AvailabilityGroupExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task CreateAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, AgTopologyConfig topology, CancellationToken ct = default);
    Task JoinAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task GrantCreateAnyDatabaseAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task DropAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Database management ───────────────────────────────
    Task<bool> DatabaseExistsOnPrimaryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task<string?> GetDatabaseAgNameAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task PrepareDatabaseForAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string backupPath, CancellationToken ct = default);
    Task AddDatabaseToAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task RemoveDatabaseFromAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task SuspendDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task ResumeDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);

    // ── Replica management ────────────────────────────────
    Task AddReplicaAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, AgReplicaConfig replica, CancellationToken ct = default);
    Task RemoveReplicaAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string replicaName, CancellationToken ct = default);
    Task SetRequiredSyncSecondariesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, int count, CancellationToken ct = default);
    Task<int?> GetRequiredSyncSecondariesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Failover ──────────────────────────────────────────
    Task FailoverAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task ForceFailoverWithDataLossAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Monitoring ────────────────────────────────────────
    Task<string?> GetLocalReplicaRoleAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<List<AgReplicaStateInfo>> GetReplicaStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<List<AgDatabaseStateInfo>> GetDatabaseStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<bool> IsPrimaryAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);

    // ── Enterprise-only features ──────────────────────────
    Task ConfigureReadOnlyRoutingAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string primaryName, List<string> secondaryNames, int listenerPort, string headlessService, string ns, CancellationToken ct = default);
    Task SetBackupPreferenceAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string preference, CancellationToken ct = default);
}
