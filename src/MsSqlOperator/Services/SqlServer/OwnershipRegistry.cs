using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>The custom resource that owns something on a server.</summary>
public sealed record ResourceOwner(string Kind, string Namespace, string Name, string Uid)
{
    public override string ToString() => $"{Kind} {Namespace}/{Name}";
}

/// <summary>A login the operator owns on a server, and the server grants it made for it.</summary>
/// <param name="Principal">The login.</param>
/// <param name="Owner">The custom resource that owns it.</param>
/// <param name="ServerGrants">JSON of the server roles and permissions granted by the operator; null
/// before any.</param>
public sealed record OwnedPrincipal(string Principal, ResourceOwner Owner, string? ServerGrants);

/// <summary>The original value of a server setting the operator changed.</summary>
public sealed record OwnedSetting(string Setting, long OriginalValue, ResourceOwner Owner);

/// <summary>
/// What the operator owns on a server, recorded in that server's <c>msdb.mssqlop</c> —
/// msdb is not replicated, so each replica carries its own. The CR status held these records alone:
/// a CR recreated by GitOps, or a status lost, forgot what had been granted and could never revoke
/// it. They are rebuilt from here.
/// </summary>
public interface ISqlOwnershipRegistry
{
    /// <summary>
    /// Claims <paramref name="principal"/> for <paramref name="owner"/> unless another resource owns
    /// it; returns the record as it stands after the claim — owned by someone else when that is the
    /// case (the caller then refuses, the login untouched).
    /// </summary>
    Task<OwnedPrincipal> ClaimPrincipalAsync(SqlConnection connection, string principal, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>The record of <paramref name="principal"/>, or null when the operator owns no such login here.</summary>
    Task<OwnedPrincipal?> GetPrincipalAsync(SqlConnection connection, string principal, CancellationToken ct = default);

    /// <summary>Hands <paramref name="principal"/> over from the owner of UID <paramref name="formerUid"/>
    /// (gone) to <paramref name="owner"/>; false when the record changed meanwhile.</summary>
    Task<bool> TakeOverPrincipalAsync(SqlConnection connection, string principal, string formerUid, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>Records the server grants the operator made for <paramref name="principal"/> (owner checked).</summary>
    Task RecordServerGrantsAsync(SqlConnection connection, string principal, string ownerUid, string serverGrants, CancellationToken ct = default);

    /// <summary>Forgets <paramref name="principal"/> once its login is dropped (owner checked).</summary>
    Task ReleasePrincipalAsync(SqlConnection connection, string principal, string ownerUid, CancellationToken ct = default);

    /// <summary>Records the value a setting had before the operator first changed it; an earlier record wins.</summary>
    Task RecordOriginalSettingAsync(SqlConnection connection, string setting, long originalValue, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>The settings recorded for <paramref name="owner"/> — by its UID, or by its kind, namespace
    /// and name: a configuration recreated by GitOps keeps its name, not its UID.</summary>
    Task<List<OwnedSetting>> GetOwnedSettingsAsync(SqlConnection connection, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>Forgets a setting once its original value is back.</summary>
    Task ForgetSettingAsync(SqlConnection connection, string setting, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>Records a trace flag the operator turned on; an earlier record wins.</summary>
    Task RecordTraceFlagAsync(SqlConnection connection, int traceFlag, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>The trace flags recorded for <paramref name="owner"/> (see <see cref="GetOwnedSettingsAsync"/>).</summary>
    Task<List<int>> GetOwnedTraceFlagsAsync(SqlConnection connection, ResourceOwner owner, CancellationToken ct = default);

    /// <summary>Forgets a trace flag once it is turned off.</summary>
    Task ForgetTraceFlagAsync(SqlConnection connection, int traceFlag, ResourceOwner owner, CancellationToken ct = default);
}

public sealed class SqlOwnershipRegistry : ISqlOwnershipRegistry
{
    private readonly ConcurrentDictionary<string, bool> _schemaReady = new();

    private async Task<SqlCommand> CommandAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        if (_schemaReady.TryAdd(connection.DataSource, true))
        {
            try { await MssqlopSchema.EnsureAsync(connection, ct); }
            catch { _schemaReady.TryRemove(connection.DataSource, out _); throw; }
        }
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private static void AddOwner(SqlCommand cmd, ResourceOwner owner)
    {
        cmd.Parameters.AddWithValue("@kind", owner.Kind);
        cmd.Parameters.AddWithValue("@ns", owner.Namespace);
        cmd.Parameters.AddWithValue("@name", owner.Name);
        cmd.Parameters.AddWithValue("@uid", owner.Uid);
    }

    private const string PrincipalColumns = "principal, owner_kind, owner_namespace, owner_name, owner_uid, server_grants";

    /// <summary>A record of the owner: same UID, or same kind, namespace and name (a recreation).</summary>
    private const string OwnedBy = "(owner_uid = @uid OR (owner_kind = @kind AND owner_namespace = @ns AND owner_name = @name))";

    private static OwnedPrincipal ReadPrincipal(SqlDataReader reader) => new(
        reader.GetString(0),
        new ResourceOwner(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    public async Task<OwnedPrincipal> ClaimPrincipalAsync(SqlConnection connection, string principal, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            IF NOT EXISTS (SELECT 1 FROM msdb.mssqlop.owned_principal WITH (UPDLOCK, HOLDLOCK) WHERE principal = @principal)
                INSERT INTO msdb.mssqlop.owned_principal (principal, owner_kind, owner_namespace, owner_name, owner_uid, recorded_at)
                VALUES (@principal, @kind, @ns, @name, @uid, SYSUTCDATETIME());
            COMMIT;
            SELECT {PrincipalColumns} FROM msdb.mssqlop.owned_principal WHERE principal = @principal;
            """, ct);
        cmd.Parameters.AddWithValue("@principal", principal);
        AddOwner(cmd, owner);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ReadPrincipal(reader)
            : throw new InvalidOperationException($"The ownership record of login {principal} could not be read back.");
    }

    public async Task<OwnedPrincipal?> GetPrincipalAsync(SqlConnection connection, string principal, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection,
            $"SELECT {PrincipalColumns} FROM msdb.mssqlop.owned_principal WHERE principal = @principal", ct);
        cmd.Parameters.AddWithValue("@principal", principal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPrincipal(reader) : null;
    }

    public async Task<bool> TakeOverPrincipalAsync(SqlConnection connection, string principal, string formerUid, ResourceOwner owner,
        CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, """
            UPDATE msdb.mssqlop.owned_principal
            SET owner_kind = @kind, owner_namespace = @ns, owner_name = @name, owner_uid = @uid, recorded_at = SYSUTCDATETIME()
            WHERE principal = @principal AND owner_uid = @former
            """, ct);
        cmd.Parameters.AddWithValue("@principal", principal);
        cmd.Parameters.AddWithValue("@former", formerUid);
        AddOwner(cmd, owner);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RecordServerGrantsAsync(SqlConnection connection, string principal, string ownerUid, string serverGrants,
        CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, """
            UPDATE msdb.mssqlop.owned_principal SET server_grants = @grants, recorded_at = SYSUTCDATETIME()
            WHERE principal = @principal AND owner_uid = @uid
            """, ct);
        cmd.Parameters.AddWithValue("@principal", principal);
        cmd.Parameters.AddWithValue("@uid", ownerUid);
        cmd.Parameters.AddWithValue("@grants", serverGrants);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleasePrincipalAsync(SqlConnection connection, string principal, string ownerUid, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection,
            "DELETE FROM msdb.mssqlop.owned_principal WHERE principal = @principal AND owner_uid = @uid", ct);
        cmd.Parameters.AddWithValue("@principal", principal);
        cmd.Parameters.AddWithValue("@uid", ownerUid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordOriginalSettingAsync(SqlConnection connection, string setting, long originalValue, ResourceOwner owner,
        CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, """
            IF NOT EXISTS (SELECT 1 FROM msdb.mssqlop.owned_setting WHERE setting = @setting)
                INSERT INTO msdb.mssqlop.owned_setting (setting, original_value, owner_kind, owner_namespace, owner_name, owner_uid, recorded_at)
                VALUES (@setting, @value, @kind, @ns, @name, @uid, SYSUTCDATETIME());
            """, ct);
        cmd.Parameters.AddWithValue("@setting", setting);
        cmd.Parameters.AddWithValue("@value", originalValue);
        AddOwner(cmd, owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<OwnedSetting>> GetOwnedSettingsAsync(SqlConnection connection, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, $"""
            SELECT setting, original_value, owner_kind, owner_namespace, owner_name, owner_uid
            FROM msdb.mssqlop.owned_setting WHERE {OwnedBy}
            """, ct);
        AddOwner(cmd, owner);
        var settings = new List<OwnedSetting>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            settings.Add(new OwnedSetting(reader.GetString(0), reader.GetInt64(1),
                new ResourceOwner(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))));
        return settings;
    }

    public async Task ForgetSettingAsync(SqlConnection connection, string setting, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection,
            $"DELETE FROM msdb.mssqlop.owned_setting WHERE setting = @setting AND {OwnedBy}", ct);
        cmd.Parameters.AddWithValue("@setting", setting);
        AddOwner(cmd, owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordTraceFlagAsync(SqlConnection connection, int traceFlag, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection, """
            IF NOT EXISTS (SELECT 1 FROM msdb.mssqlop.owned_traceflag WHERE traceflag = @flag)
                INSERT INTO msdb.mssqlop.owned_traceflag (traceflag, owner_kind, owner_namespace, owner_name, owner_uid, recorded_at)
                VALUES (@flag, @kind, @ns, @name, @uid, SYSUTCDATETIME());
            """, ct);
        cmd.Parameters.AddWithValue("@flag", traceFlag);
        AddOwner(cmd, owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<int>> GetOwnedTraceFlagsAsync(SqlConnection connection, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection,
            $"SELECT traceflag FROM msdb.mssqlop.owned_traceflag WHERE {OwnedBy} ORDER BY traceflag", ct);
        AddOwner(cmd, owner);
        var flags = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            flags.Add(reader.GetInt32(0));
        return flags;
    }

    public async Task ForgetTraceFlagAsync(SqlConnection connection, int traceFlag, ResourceOwner owner, CancellationToken ct = default)
    {
        await using var cmd = await CommandAsync(connection,
            $"DELETE FROM msdb.mssqlop.owned_traceflag WHERE traceflag = @flag AND {OwnedBy}", ct);
        cmd.Parameters.AddWithValue("@flag", traceFlag);
        AddOwner(cmd, owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
