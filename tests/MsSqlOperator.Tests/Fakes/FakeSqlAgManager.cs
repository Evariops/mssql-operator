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
    public readonly List<string> SuspendDataMovementCalls = [];
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

    /// <summary>Database files and their volume totals, per connection host; none by default.</summary>
    public Func<SqlConnection, List<FileVolumeInfo>>? FileVolumesResolver { get; set; }
    public Task<List<FileVolumeInfo>> GetFileVolumesAsync(SqlConnection connection, CancellationToken ct = default)
        => Task.FromResult(FileVolumesResolver?.Invoke(connection) ?? []);

    /// <summary>Uptime every instance reports; long by default — an instance that has settled its role.</summary>
    public TimeSpan InstanceUptime { get; set; } = TimeSpan.FromHours(1);
    public Task<TimeSpan> GetInstanceUptimeAsync(SqlConnection connection, CancellationToken ct = default) => Task.FromResult(InstanceUptime);

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

    /// <summary>Makes the demotion (OFFLINE) fail on the connections it returns true for: a primary
    /// that answers but refuses to give up the role.</summary>
    public Func<SqlConnection, bool>? DemoteFails { get; set; }

    /// <summary>Host of every connection SET (ROLE = SECONDARY) ran on, in order.</summary>
    public readonly List<string> SetSecondaryHosts = [];

    public string? EndpointKeyRecovery { get; set; }
    public readonly List<string> EndpointKeyChecks = [];

    public Task DemoteToSecondaryAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        DemoteCalls.Add(agName);
        if (DemoteFails?.Invoke(connection) == true)
            throw new InvalidOperationException("fake: the availability group resource did not come online");
        var host = connection.DataSource ?? string.Empty;
        if (host.Length > 0) _demoted.Add(host);
        return Task.CompletedTask;
    }

    /// <summary>Number of SET (ROLE = SECONDARY) calls that fail before one succeeds (41104 right
    /// after OFFLINE).</summary>
    public int SetSecondaryFailures { get; set; }

    /// <summary>Host of every connection an endpoint restart ran on.</summary>
    public readonly List<string> EndpointRestartHosts = [];

    public Task RestartEndpointAsync(SqlConnection connection, string endpointName, CancellationToken ct = default)
    {
        EndpointRestartHosts.Add(connection.DataSource ?? string.Empty);
        return Task.CompletedTask;
    }

    public Task SetSecondaryRoleAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        var host = connection.DataSource ?? string.Empty;
        SetSecondaryHosts.Add(host);
        if (SetSecondaryFailures > 0)
        {
            SetSecondaryFailures--;
            throw new InvalidOperationException("fake: 41104 the availability group resource did not come online");
        }
        if (host.Length > 0)
        {
            _promoted.Remove(host);
            _demoted.Add(host);
        }
        return Task.CompletedTask;
    }

    /// <summary>Thrown by the endpoint key recovery (a key that stays unusable).</summary>
    public Exception? EndpointKeyFailure { get; set; }

    public Task<string?> EnsureEndpointKeyUsableAsync(SqlConnection connection, string endpointName, string masterKeyPassword, CancellationToken ct = default)
    {
        EndpointKeyChecks.Add(connection.DataSource ?? string.Empty);
        if (EndpointKeyFailure is not null)
            throw EndpointKeyFailure;
        return Task.FromResult(EndpointKeyRecovery);
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
    /// <summary>Public certificate bytes per (connection, certificate); empty when absent.</summary>
    public Func<SqlConnection, string, byte[]> CertificateResolver { get; set; } = (_, _) => [];
    public Task<byte[]> ExportHadrPublicCertificateAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(CertificateResolver(c, certName));
    /// <summary>(host, certificate) of every trusted-certificate import / removal and every JOIN.</summary>
    public readonly List<(string Host, string Cert)> ImportTrustedCalls = [];
    public readonly List<(string Host, string Cert)> RemoveTrustedCalls = [];
    public readonly List<string> JoinHosts = [];
    public Task ImportTrustedReplicaCertificateAsync(SqlConnection c, string certName, byte[] publicCertBytes, CancellationToken ct = default)
    {
        ImportTrustedCalls.Add((c.DataSource ?? string.Empty, certName));
        return Task.CompletedTask;
    }
    public Task RemoveTrustedReplicaAsync(SqlConnection c, string loginName, string certName, CancellationToken ct = default)
    {
        RemoveTrustedCalls.Add((c.DataSource ?? string.Empty, certName));
        return Task.CompletedTask;
    }
    public Task<bool> CertificateExistsAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetCertificateThumbprintAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> LoginExistsAsync(SqlConnection c, string loginName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateEndpointLoginFromCertificateAsync(SqlConnection c, string loginName, string certName, CancellationToken ct = default) => Task.CompletedTask;
    public bool EndpointExists { get; set; }
    public Task<bool> EndpointExistsAsync(SqlConnection c, string endpointName, CancellationToken ct = default) => Task.FromResult(EndpointExists);
    public Task CreateEndpointAsync(SqlConnection c, string endpointName, int port, string certName, string algorithm, CancellationToken ct = default) => Task.CompletedTask;
    public Task GrantEndpointConnectAsync(SqlConnection c, string endpointName, string login, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AvailabilityGroupExistsAsync(SqlConnection c, string agName, CancellationToken ct = default)
        => Task.FromResult(AgExistsResolver is not null ? AgExistsResolver(c, agName) : false);
    public Task CreateAvailabilityGroupAsync(SqlConnection c, AgTopologyConfig topology, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Replicas removed from / added to the AG on the primary, local AGs dropped (by host).</summary>
    public readonly List<string> RemovedReplicas = [];
    public readonly List<string> AddedReplicas = [];
    public readonly List<string> DroppedLocalAgHosts = [];
    public List<string> AgDatabaseNames { get; set; } = [];
    public List<HeldLogInfo> HeldLogs { get; set; } = [];

    public Task RemoveReplicaAsync(SqlConnection c, string agName, string replica, CancellationToken ct = default)
    {
        RemovedReplicas.Add(replica);
        return Task.CompletedTask;
    }

    public Task AddReplicaAsync(SqlConnection c, string agName, AgReplicaConfig replica, CancellationToken ct = default)
    {
        AddedReplicas.Add(replica.Name);
        return Task.CompletedTask;
    }

    public Task DropLocalAvailabilityGroupAsync(SqlConnection c, string agName, CancellationToken ct = default)
    {
        DroppedLocalAgHosts.Add(c.DataSource ?? string.Empty);
        return Task.CompletedTask;
    }

    public Task<List<string>> GetAgDatabaseNamesAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(AgDatabaseNames);
    public Task<List<HeldLogInfo>> GetLogsHeldByReplicasAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(HeldLogs);

    public Task JoinAvailabilityGroupAsync(SqlConnection c, string agName, CancellationToken ct = default)
    {
        JoinHosts.Add(c.DataSource ?? string.Empty);
        return Task.CompletedTask;
    }
    public Task GrantCreateAnyDatabaseAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public bool DatabaseExistsOnPrimary { get; set; }
    public Task<bool> DatabaseExistsOnPrimaryAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(DatabaseExistsOnPrimary);
    public Task<string?> GetDatabaseAgNameAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> PrepareDatabaseForAgAsync(SqlConnection c, string database, string backupPath, CancellationToken ct = default)
    {
        PrepareDatabaseCalls.Add(database);
        return Task.FromResult(!LogChainEstablished);
    }
    public Task<bool> HasLogChainAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(LogChainEstablished);
    public Task AddDatabaseToAgAsync(SqlConnection c, string agName, string database, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveDatabaseFromAgAsync(SqlConnection c, string agName, string database, CancellationToken ct = default)
    {
        RemoveDatabaseFromAgCalls.Add((agName, database));
        return Task.CompletedTask;
    }
    /// <summary>(host, database) of every secondary copy dropped, and (host, replica) of every seeding
    /// requested on a primary.</summary>
    public readonly List<(string Host, string Database)> DropSecondaryCopyCalls = [];
    public readonly List<(string Host, string Replica)> SeedingRequests = [];
    /// <summary>Latest seeding per replica; none by default.</summary>
    public Func<string, ReplicaSeedingInfo?> ReplicaSeedingResolver { get; set; } = _ => null;
    public Task<ReplicaSeedingInfo?> GetReplicaSeedingAsync(SqlConnection c, string agName, string database, string replica, CancellationToken ct = default)
        => Task.FromResult(ReplicaSeedingResolver(replica));

    /// <summary>File space per connection (empty by default: the space check is skipped).</summary>
    public Func<SqlConnection, List<DatabaseFileSpace>> FileSpaceResolver { get; set; } = _ => [];

    /// <summary>Makes the drop refuse the copy (online and writable outside the AG) on the
    /// connections it returns true for.</summary>
    public Func<SqlConnection, bool>? DropSecondaryCopyRefused { get; set; }

    public Task DropSecondaryCopyAsync(SqlConnection c, string database, CancellationToken ct = default)
    {
        DropSecondaryCopyCalls.Add((c.DataSource ?? string.Empty, database));
        if (DropSecondaryCopyRefused?.Invoke(c) == true)
            throw new SecondaryCopyNotDroppedException(database, new InvalidOperationException("fake: 51010"));
        return Task.CompletedTask;
    }

    /// <summary>User databases per connection (none by default: a blank replica).</summary>
    public Func<SqlConnection, List<string>> UserDatabasesResolver { get; set; } = _ => [];

    public Task<List<string>> GetUserDatabaseNamesAsync(SqlConnection c, CancellationToken ct = default)
        => Task.FromResult(UserDatabasesResolver(c));

    public Task RequestSeedingAsync(SqlConnection c, string agName, string replica, CancellationToken ct = default)
    {
        SeedingRequests.Add((c.DataSource ?? string.Empty, replica));
        return Task.CompletedTask;
    }

    public Task<List<DatabaseFileSpace>> GetDatabaseFileSpaceAsync(SqlConnection c, string database, CancellationToken ct = default)
        => Task.FromResult(FileSpaceResolver(c));

    /// <summary>(host, database) of every log backup, (host, folder, cutoff) of every cleanup.</summary>
    public readonly List<(string Host, string Database)> BackupLogCalls = [];
    public readonly List<(string Host, string Folder, DateTime Cutoff)> DeleteOldBackupsCalls = [];
    public bool BackupLogFails { get; set; }
    /// <summary>What a failing log backup throws; an unspecified failure by default.</summary>
    public Exception? BackupLogException { get; set; }
    public LogUsageInfo? LogUsageAfterBackup { get; set; }

    public Task<string> BackupLogAsync(SqlConnection c, string database, string folder, DateTime utcNow, CancellationToken ct = default)
    {
        if (BackupLogFails)
            throw BackupLogException ?? new InvalidOperationException("fake: the backup volume is full");
        BackupLogCalls.Add((c.DataSource ?? string.Empty, database));
        // Once backed up, the log has room again — unless the secondaries still hold it.
        if (LogUsage is { } usage)
            LogUsage = LogUsageAfterBackup ?? usage with { PercentUsed = 3, ReuseWait = "NOTHING" };
        return Task.FromResult($"{folder}/{database}.trn");
    }

    public Task DeleteOldBackupsAsync(SqlConnection c, string folder, string extension, DateTime cutoff, CancellationToken ct = default)
    {
        DeleteOldBackupsCalls.Add((c.DataSource ?? string.Empty, folder, cutoff));
        return Task.CompletedTask;
    }

    /// <summary>Log usage returned for any database; null by default.</summary>
    public LogUsageInfo? LogUsage { get; set; }
    public Task<LogUsageInfo?> GetLogUsageAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(LogUsage);

    public Task SuspendDataMovementAsync(SqlConnection c, string database, CancellationToken ct = default)
    {
        SuspendDataMovementCalls.Add(database);
        return Task.CompletedTask;
    }
    /// <summary>Makes HADR RESUME throw (a copy no longer joined to the AG).</summary>
    public bool ResumeFails { get; set; }

    public Task ResumeDataMovementAsync(SqlConnection c, string database, CancellationToken ct = default)
    {
        if (ResumeFails)
            throw new InvalidOperationException("fake: the database is not joined to an availability group");
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
    /// <summary>Every read-only routing list configured on a primary, in order.</summary>
    public readonly List<List<string>> ReadOnlyRoutingLists = [];
    public Task ConfigureReadOnlyRoutingAsync(SqlConnection c, string agName, string primaryName, List<string> secondaryNames, string headlessService, string ns, CancellationToken ct = default,
        bool endWithPrimary = false)
    {
        ReadOnlyRoutingLists.Add(endWithPrimary ? [.. secondaryNames, primaryName] : [.. secondaryNames]);
        return Task.CompletedTask;
    }
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

    /// <summary>Database-level extended properties, keyed by (host, database, name).</summary>
    public Dictionary<(string Database, string Name), string> DatabaseProperties { get; } = [];

    public Task<string?> ReadDatabasePropertyAsync(SqlConnection connection, string database, string name, CancellationToken ct = default)
        => Task.FromResult(DatabaseProperties.TryGetValue((database, name), out var v) ? v : null);

    public Task WriteDatabasePropertyAsync(SqlConnection connection, string database, string name, string? value, CancellationToken ct = default)
    {
        if (value is null) DatabaseProperties.Remove((database, name));
        else DatabaseProperties[(database, name)] = value;
        return Task.CompletedTask;
    }
}
