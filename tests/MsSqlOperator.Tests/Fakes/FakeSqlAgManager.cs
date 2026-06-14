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
    public readonly List<(string Ag, string Database)> RemoveDatabaseFromAgCalls = [];
    public readonly List<string> ResumeDataMovementCalls = [];
    public string? LocalReplicaRole { get; set; } = "PRIMARY";
    public List<AgDatabaseStateInfo> DatabaseStates { get; set; } = [];

    public Task FailoverAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        FailoverCalls.Add(agName);
        return Task.CompletedTask;
    }

    public Task ForceFailoverWithDataLossAsync(SqlConnection connection, string agName, CancellationToken ct = default)
    {
        ForceFailoverCalls.Add(agName);
        return Task.CompletedTask;
    }

    public Task<string?> GetLocalReplicaRoleAsync(SqlConnection connection, string agName, CancellationToken ct = default)
        => Task.FromResult(LocalReplicaRole);

    // ── Inert stubs (unused by the orchestrator unit tests) ──────────────
    public Task<bool> MasterKeyExistsAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateMasterKeyAsync(SqlConnection c, string password, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateHadrCertificateAsync(SqlConnection c, string certName, string subject, CancellationToken ct = default) => Task.CompletedTask;
    public Task ImportHadrCertificateAsync(SqlConnection c, string certName, string certPath, string keyPath, string decryptionPassword, CancellationToken ct = default) => Task.CompletedTask;
    public Task ExportHadrCertificateAsync(SqlConnection c, string certName, string certPath, string keyPath, string encryptionPassword, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]> ExportHadrPublicCertificateAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task ImportTrustedReplicaCertificateAsync(SqlConnection c, string certName, byte[] publicCertBytes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> CertificateExistsAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetCertificateThumbprintAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<bool> LoginExistsAsync(SqlConnection c, string loginName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateEndpointLoginFromCertificateAsync(SqlConnection c, string loginName, string certName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> EndpointExistsAsync(SqlConnection c, string endpointName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateEndpointAsync(SqlConnection c, string endpointName, int port, string certName, string algorithm, CancellationToken ct = default) => Task.CompletedTask;
    public Task GrantEndpointConnectAsync(SqlConnection c, string endpointName, string login, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AvailabilityGroupExistsAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(false);
    public Task CreateAvailabilityGroupAsync(SqlConnection c, AgTopologyConfig topology, CancellationToken ct = default) => Task.CompletedTask;
    public Task JoinAvailabilityGroupAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public Task GrantCreateAnyDatabaseAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public Task DropAvailabilityGroupAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> DatabaseExistsOnPrimaryAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetDatabaseAgNameAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task PrepareDatabaseForAgAsync(SqlConnection c, string database, string backupPath, CancellationToken ct = default) => Task.CompletedTask;
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
    public Task AddReplicaAsync(SqlConnection c, string agName, AgReplicaConfig replica, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveReplicaAsync(SqlConnection c, string agName, string replicaName, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetRequiredSyncSecondariesAsync(SqlConnection c, string agName, int count, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int?> GetRequiredSyncSecondariesAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult<int?>(null);
    public Task<List<AgReplicaStateInfo>> GetReplicaStatesAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(new List<AgReplicaStateInfo>());
    public Task<List<AgDatabaseStateInfo>> GetDatabaseStatesAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(DatabaseStates);
    public Task<bool> IsPrimaryAsync(SqlConnection c, string agName, CancellationToken ct = default) => Task.FromResult(false);
    public Task ConfigureReadOnlyRoutingAsync(SqlConnection c, string agName, string primaryName, List<string> secondaryNames, int listenerPort, string headlessService, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetBackupPreferenceAsync(SqlConnection c, string agName, string preference, CancellationToken ct = default) => Task.CompletedTask;
}
