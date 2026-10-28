using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlOwnershipRegistry"/>: one in-memory msdb.mssqlop per
/// server (the connection's data source).</summary>
public sealed class FakeOwnershipRegistry : ISqlOwnershipRegistry
{
    public readonly Dictionary<(string Server, string Principal), OwnedPrincipal> Principals = [];
    public readonly Dictionary<(string Server, string Setting), OwnedSetting> Settings = [];
    public readonly Dictionary<(string Server, int Flag), ResourceOwner> TraceFlags = [];

    private static string Server(SqlConnection c) => c.DataSource ?? "";

    public Task<OwnedPrincipal> ClaimPrincipalAsync(SqlConnection c, string principal, ResourceOwner owner, CancellationToken ct = default)
    {
        var key = (Server(c), principal);
        if (!Principals.TryGetValue(key, out var record))
            Principals[key] = record = new OwnedPrincipal(principal, owner, null);
        return Task.FromResult(record);
    }

    public Task<OwnedPrincipal?> GetPrincipalAsync(SqlConnection c, string principal, CancellationToken ct = default)
        => Task.FromResult(Principals.GetValueOrDefault((Server(c), principal)));

    public Task<bool> TakeOverPrincipalAsync(SqlConnection c, string principal, string formerUid, ResourceOwner owner, CancellationToken ct = default)
    {
        var key = (Server(c), principal);
        if (!Principals.TryGetValue(key, out var record) || record.Owner.Uid != formerUid)
            return Task.FromResult(false);
        Principals[key] = record with { Owner = owner };
        return Task.FromResult(true);
    }

    public Task RecordServerGrantsAsync(SqlConnection c, string principal, string ownerUid, string serverGrants, CancellationToken ct = default)
    {
        var key = (Server(c), principal);
        if (Principals.TryGetValue(key, out var record) && record.Owner.Uid == ownerUid)
            Principals[key] = record with { ServerGrants = serverGrants };
        return Task.CompletedTask;
    }

    public Task ReleasePrincipalAsync(SqlConnection c, string principal, string ownerUid, CancellationToken ct = default)
    {
        var key = (Server(c), principal);
        if (Principals.TryGetValue(key, out var record) && record.Owner.Uid == ownerUid)
            Principals.Remove(key);
        return Task.CompletedTask;
    }

    public Task RecordOriginalSettingAsync(SqlConnection c, string setting, long originalValue, ResourceOwner owner, CancellationToken ct = default)
    {
        Settings.TryAdd((Server(c), setting), new OwnedSetting(setting, originalValue, owner));
        return Task.CompletedTask;
    }

    public Task<List<OwnedSetting>> GetOwnedSettingsAsync(SqlConnection c, string ownerUid, CancellationToken ct = default)
        => Task.FromResult(Settings.Where(e => e.Key.Server == Server(c) && e.Value.Owner.Uid == ownerUid).Select(e => e.Value).ToList());

    public Task ForgetSettingAsync(SqlConnection c, string setting, string ownerUid, CancellationToken ct = default)
    {
        if (Settings.TryGetValue((Server(c), setting), out var record) && record.Owner.Uid == ownerUid)
            Settings.Remove((Server(c), setting));
        return Task.CompletedTask;
    }

    public Task RecordTraceFlagAsync(SqlConnection c, int traceFlag, ResourceOwner owner, CancellationToken ct = default)
    {
        TraceFlags.TryAdd((Server(c), traceFlag), owner);
        return Task.CompletedTask;
    }

    public Task<List<int>> GetOwnedTraceFlagsAsync(SqlConnection c, string ownerUid, CancellationToken ct = default)
        => Task.FromResult(TraceFlags.Where(e => e.Key.Server == Server(c) && e.Value.Uid == ownerUid).Select(e => e.Key.Flag).Order().ToList());

    public Task ForgetTraceFlagAsync(SqlConnection c, int traceFlag, string ownerUid, CancellationToken ct = default)
    {
        if (TraceFlags.TryGetValue((Server(c), traceFlag), out var owner) && owner.Uid == ownerUid)
            TraceFlags.Remove((Server(c), traceFlag));
        return Task.CompletedTask;
    }
}
