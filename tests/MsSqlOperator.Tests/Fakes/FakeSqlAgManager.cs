using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for the large <see cref="ISqlAgManager"/> interface. Records the
/// failover operations and serves a configurable local-replica role; all other members are inert
/// stubs returning sensible defaults (enough for the orchestrator unit tests).</summary>
public sealed class FakeSqlAgManager : ISqlAgManager
{
    public readonly List<string> FailoverCalls = [];
    public readonly List<string> ForceFailoverCalls = [];
    public readonly List<string> DemoteCalls = [];
    public readonly List<(string Ag, string Database)> RemoveDatabaseFromAgCalls = [];
    public readonly List<string> ResumeDataMovementCalls = [];
    public readonly List<(string Ag, int Count)> SetRequiredSyncCalls = [];
    public readonly List<string> PrepareDatabaseCalls = [];
    public string? LocalReplicaRole { get; set; } = "PRIMARY";
    public List<AgDatabaseStateInfo> DatabaseStates { get; set; } = [];
    public string? ProductVersion { get; set; } = "16.0.4125.3";
    public string? RecoveryModel { get; set; } = "FULL";
    public AgSeedingStateInfo? SeedingState { get; set; }
    public (long DatabaseSizeMb, long FreeSpaceMb)? BackupSpace { get; set; }
    public bool LogChainEstablished { get; set; } = true;

    /// <summary>Per-connection role after a demotion: the orchestrator re-reads the
    /// role to confirm SET (ROLE = SECONDARY) landed.</summary>
    public Func<SqlConnection, string?>? RoleAfterDemoteResolver { get; set; }
    private readonly HashSet<string> _demoted = [];
    private readonly HashSet<string> _promoted = [];

    // ── Opt-in per-connection resolvers (backward compatible: null ⇒ the global defaults above) ──
    // The AgHealthChecker probes every replica by FQDN and calls these on each per-replica
    // connection. Resolving by connection lets a test model split-brain (two replicas reporting
    // PRIMARY), missing membership, etc. Connections carry their host in DataSource when the
    // FakeSqlConnectionFactory is set up with ConnectionForHost = h => new SqlConnection($"Server={h}").
    public Func<SqlConnection, string, bool>? AgExistsResolver { get; set; }
    public Func<SqlConnection, string?>? RoleResolver { get; set; }
    public Func<SqlConnection, List<AgReplicaStateInfo>>? ReplicaStatesResolver { get; set; }
    public Func<SqlConnection, List<AgDatabaseStateInfo>>? DatabaseStatesResolver { get; set; }

    /// <summary>Host of every connection a promotion command (FailoverAsync or
    /// ForceFailoverWithDataLossAsync) ran on, in order.</summary>
    public readonly List<string> PromotionHosts = [];

    /// <summary>Makes the promotion command fail on the connections it returns true for.</summary>
    public Func<SqlConnection, bool>? PromotionFails { get; set; }

    public Task FailoverAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        FailoverCalls.Add(agName);
        Promote(connection);
        return Task.CompletedTask;
    }

    public Task ForceFailoverWithDataLossAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        ForceFailoverCalls.Add(agName);
        Promote(connection);
        return Task.CompletedTask;
    }

    private void Promote(SqlConnection connection)
    {
        PromotionHosts.Add(connection.DataSource ?? string.Empty);
        if (PromotionFails?.Invoke(connection) == true)
            throw new InvalidOperationException("fake: promotion failed");
        MarkPromoted(connection);
    }

    /// <summary>A replica that received a failover command reports PRIMARY afterwards — the
    /// orchestrator re-reads the role to confirm the promotion landed.</summary>
    private void MarkPromoted(SqlConnection connection)
    {
        var host = connection.DataSource ?? string.Empty;
        if (host.Length == 0) return;
        _promoted.Add(host);
        _demoted.Remove(host);
    }

    public Task<string?> GetLocalReplicaRoleAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        // Once demoted, a replica reports SECONDARY — that is exactly what the orchestrator re-reads
        // to confirm SET (ROLE = SECONDARY) actually landed.
        var host = connection.DataSource ?? string.Empty;
        if (host.Length > 0 && _promoted.Contains(host))
            return Task.FromResult<string?>("PRIMARY");
        if (host.Length > 0 && _demoted.Contains(host))
            return Task.FromResult<string?>("SECONDARY");
        return Task.FromResult(RoleResolver is not null ? RoleResolver(connection) : LocalReplicaRole);
    }

    public Task DemoteToSecondaryAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        DemoteCalls.Add(agName);
        var host = connection.DataSource ?? string.Empty;
        if (host.Length > 0) _demoted.Add(host);
        return Task.CompletedTask;
    }

    public Task<string?> GetProductVersionAsync(SqlConnection connection, CancellationToken ct = default)
        => Task.FromResult(ProductVersion);

    public Task<string?> GetRecoveryModelAsync(SqlConnection c, string database, CancellationToken ct = default)
        => Task.FromResult(RecoveryModel);

    public Task<(long DatabaseSizeMb, long FreeSpaceMb)?> GetBackupSpaceEstimateAsync(SqlConnection c, string database, string backupPath, CancellationToken ct = default)
        => Task.FromResult(BackupSpace);

    public Task<AgSeedingStateInfo?> GetSeedingStateAsync(SqlConnection c, string agName, string database, CancellationToken ct = default)
        => Task.FromResult(SeedingState);

    // ── Inert stubs (unused by the orchestrator unit tests) ──────────────
    public Task<bool> MasterKeyExistsAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateMasterKeyAsync(SqlConnection c, string password, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateHadrCertificateAsync(SqlConnection c, string certName, string subject, string? expiryDate = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> ExportHadrPublicCertificateAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task ImportTrustedReplicaCertificateAsync(SqlConnection c, string certName, byte[] publicCertBytes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> CertificateExistsAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetCertificateThumbprintAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> LoginExistsAsync(SqlConnection c, string loginName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateEndpointLoginFromCertificateAsync(SqlConnection c, string loginName, string certName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> EndpointExistsAsync(SqlConnection c, string endpointName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateEndpointAsync(SqlConnection c, string endpointName, int port, string certName, string algorithm, CancellationToken ct = default) => Task.CompletedTask;
    public Task GrantEndpointConnectAsync(SqlConnection c, string endpointName, string login, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AvailabilityGroupExistsAsync(SqlConnection c, string agName, CancellationToken ct = default)
        => Task.FromResult(AgExistsResolver is not null ? AgExistsResolver(c, agName) : false);
    public Task CreateAvailabilityGroupAsync(SqlConnection c, AgTopologyConfig topology, CancellationToken ct = default) => Task.CompletedTask;
    public Task JoinAvailabilityGroupAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public Task GrantCreateAnyDatabaseAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> DatabaseExistsOnPrimaryAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetDatabaseAgNameAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> PrepareDatabaseForAgAsync(SqlConnection c, string database, string backupPath, CancellationToken ct = default)
    {
        PrepareDatabaseCalls.Add(database);
        return Task.FromResult(!LogChainEstablished);
    }
    public Task AddDatabaseToAgAsync(SqlConnection c, string agName, string database, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveDatabaseFromAgAsync(SqlConnection c, string agName, string database, CancellationToken ct = default)
    {
        RemoveDatabaseFromAgCalls.Add((agName, database));
        return Task.CompletedTask;
    }
    public Task SuspendDataMovementAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeDataMovementAsync(SqlConnection c, string database, CancellationToken ct = default)
    {
        ResumeDataMovementCalls.Add(database);
        return Task.CompletedTask;
    }
    public Task SetRequiredSyncSecondariesAsync(SqlConnection c, string agName, int count, CancellationToken ct = default)
    {
        SetRequiredSyncCalls.Add((agName, count));
        return Task.CompletedTask;
    }
    public int? RequiredSyncSecondaries { get; set; }
    public Task<int?> GetRequiredSyncSecondariesAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(RequiredSyncSecondaries);
    public Task<List<AgReplicaStateInfo>> GetReplicaStatesAsync(SqlConnection c, string agName, CancellationToken ct = default)
        => Task.FromResult(ReplicaStatesResolver is not null ? ReplicaStatesResolver(c) : new List<AgReplicaStateInfo>());
    public Task<List<AgDatabaseStateInfo>> GetDatabaseStatesAsync(SqlConnection c, string agName, CancellationToken ct = default)
        => Task.FromResult(DatabaseStatesResolver is not null ? DatabaseStatesResolver(c) : DatabaseStates);
    public Task<bool> IsPrimaryAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(false);
    public Task ConfigureReadOnlyRoutingAsync(SqlConnection c, string agName, string primaryName, List<string> secondaryNames, string headlessService, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetBackupPreferenceAsync(SqlConnection c, string agName, string preference, CancellationToken ct = default)
    {
        BackupPreference = SqlAgManager.ToSqlBackupPreference(preference);
        SetBackupPreferenceCalls.Add(preference);
        return Task.CompletedTask;
    }

    public readonly List<string> SetBackupPreferenceCalls = [];
    public string? BackupPreference { get; set; } = "SECONDARY";
    public Task<string?> GetBackupPreferenceAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(BackupPreference);

    /// <summary>Configuration sequence number per connection (a planned failover requires equal numbers); equal by default.</summary>
    public Func<SqlConnection, long?> SequenceNumberResolver { get; set; } = _ => 1;
    public Task<long?> GetAgSequenceNumberAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(SequenceNumberResolver(c));
}
