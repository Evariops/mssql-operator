using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlUserManager"/>: records the mutating calls (with
/// their arguments) for interaction assertions, and serves configurable results for the queries.</summary>
public sealed class FakeSqlUserManager : ISqlUserManager
{
    // Recorded mutating calls.
    public readonly List<(string Login, string Pwd, string? DefaultDb, bool CheckPolicy, bool CheckExpiration, byte[]? Sid)> CreateLoginCalls = [];
    public readonly List<(string Login, string Pwd)> AlterLoginPasswordCalls = [];
    public readonly List<(string Db, string User, string Login)> CreateDatabaseUserCalls = [];
    public readonly List<(string Db, string User, string Role)> AddRoleMemberCalls = [];
    public readonly List<(string Db, string User, string Schema, string Permission)> GrantSchemaPermissionCalls = [];
    public readonly List<(string Login, string Permission)> GrantServerPermissionCalls = [];
    public readonly List<(string Login, string Role)> AddServerRoleMemberCalls = [];
    public readonly List<string> DropLoginCalls = [];
    public readonly List<(string Db, string User)> DropDatabaseUserCalls = [];
    public readonly List<(string Db, string User)> TransferSchemaOwnershipCalls = [];
    public readonly List<string> DisableLoginCalls = [];
    public readonly List<string> EnableLoginCalls = [];
    public readonly List<string> KillSessionsCalls = [];
    public readonly List<(string Old, string New)> RenameLoginCalls = [];
    public readonly List<(string Login, string Role)> DropServerRoleMemberCalls = [];
    public readonly List<(string Login, string Permission)> RevokeServerPermissionCalls = [];
    public readonly List<(string Db, string User, string Role)> DropRoleMemberCalls = [];
    public readonly List<(string Db, string User, string Schema, string Permission)> RevokeSchemaPermissionCalls = [];

    // Configurable query results.
    public Func<string, bool> LoginExists { get; set; } = _ => false;
    public Func<string, string, bool> DatabaseUserExists { get; set; } = (_, _) => false;
    public Func<string, string, List<string>> DatabaseRoleMemberships { get; set; } = (_, _) => [];
    public Func<string, List<string>> ServerRoleMemberships { get; set; } = _ => [];
    public byte[]? LoginSid { get; set; }
    /// <summary>PWDCOMPARE result: by default the stored password always matches (no ALTER).</summary>
    public Func<string, string, bool> PasswordMatches { get; set; } = (_, _) => true;
    public bool? LoginEnabled { get; set; }
    public int KillSessionsResult { get; set; }

    public Task<bool> LoginExistsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
        => Task.FromResult(LoginExists(loginName));

    public Task CreateLoginAsync(SqlConnection connection, string loginName, string password, string? defaultDatabase = null,
        bool checkPolicy = true, bool checkExpiration = false, byte[]? sid = null, CancellationToken ct = default)
    {
        CreateLoginCalls.Add((loginName, password, defaultDatabase, checkPolicy, checkExpiration, sid));
        return Task.CompletedTask;
    }

    public Task AlterLoginPasswordAsync(SqlConnection connection, string loginName, string password, CancellationToken ct = default)
    {
        AlterLoginPasswordCalls.Add((loginName, password));
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetLoginSidAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
        => Task.FromResult(LoginSid);

    public Task DisableLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        DisableLoginCalls.Add(loginName);
        return Task.CompletedTask;
    }

    public Task EnableLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        EnableLoginCalls.Add(loginName);
        return Task.CompletedTask;
    }

    public Task<int> KillSessionsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        KillSessionsCalls.Add(loginName);
        return Task.FromResult(KillSessionsResult);
    }

    public Task<bool?> IsLoginEnabledAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
        => Task.FromResult(LoginEnabled);

    public Task<List<string>> GetServerRoleMembershipsAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
        => Task.FromResult(ServerRoleMemberships(loginName));

    public Task<List<string>> GetDatabaseRoleMembershipsAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
        => Task.FromResult(DatabaseRoleMemberships(database, userName));

    public Task<Dictionary<string, List<string>>> GetDatabaseUserRolesAsync(SqlConnection connection,
        IReadOnlyCollection<string> databases, string userName, CancellationToken ct = default)
        => Task.FromResult(databases
            .Where(db => DatabaseUserExists(db, userName))
            .ToDictionary(db => db, db => DatabaseRoleMemberships(db, userName), StringComparer.OrdinalIgnoreCase));

    public Task<bool> DatabaseUserExistsAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
        => Task.FromResult(DatabaseUserExists(database, userName));

    public Task CreateDatabaseUserAsync(SqlConnection connection, string database, string userName, string loginName, CancellationToken ct = default)
    {
        CreateDatabaseUserCalls.Add((database, userName, loginName));
        return Task.CompletedTask;
    }

    public Task AddRoleMemberAsync(SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default)
    {
        AddRoleMemberCalls.Add((database, userName, roleName));
        return Task.CompletedTask;
    }

    public Task GrantSchemaPermissionAsync(SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default)
    {
        GrantSchemaPermissionCalls.Add((database, userName, schema, permission));
        return Task.CompletedTask;
    }

    public Task GrantServerPermissionAsync(SqlConnection connection, string loginName, string permission, CancellationToken ct = default)
    {
        GrantServerPermissionCalls.Add((loginName, permission));
        return Task.CompletedTask;
    }

    public Task AddServerRoleMemberAsync(SqlConnection connection, string loginName, string roleName, CancellationToken ct = default)
    {
        AddServerRoleMemberCalls.Add((loginName, roleName));
        return Task.CompletedTask;
    }

    public Task RenameLoginAsync(SqlConnection connection, string oldName, string newName, CancellationToken ct = default)
    {
        RenameLoginCalls.Add((oldName, newName));
        return Task.CompletedTask;
    }

    public Task DropLoginAsync(SqlConnection connection, string loginName, CancellationToken ct = default)
    {
        DropLoginCalls.Add(loginName);
        return Task.CompletedTask;
    }

    public Task DropDatabaseUserAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        DropDatabaseUserCalls.Add((database, userName));
        return Task.CompletedTask;
    }

    public Task TransferSchemaOwnershipAsync(SqlConnection connection, string database, string userName, CancellationToken ct = default)
    {
        TransferSchemaOwnershipCalls.Add((database, userName));
        return Task.CompletedTask;
    }

    public Task DropServerRoleMemberAsync(SqlConnection connection, string loginName, string roleName, CancellationToken ct = default)
    {
        DropServerRoleMemberCalls.Add((loginName, roleName));
        return Task.CompletedTask;
    }

    public Task RevokeServerPermissionAsync(SqlConnection connection, string loginName, string permission, CancellationToken ct = default)
    {
        RevokeServerPermissionCalls.Add((loginName, permission));
        return Task.CompletedTask;
    }

    public Task DropRoleMemberAsync(SqlConnection connection, string database, string userName, string roleName, CancellationToken ct = default)
    {
        DropRoleMemberCalls.Add((database, userName, roleName));
        return Task.CompletedTask;
    }

    public Task RevokeSchemaPermissionAsync(SqlConnection connection, string database, string userName, string schema, string permission, CancellationToken ct = default)
    {
        RevokeSchemaPermissionCalls.Add((database, userName, schema, permission));
        return Task.CompletedTask;
    }

    public Task<bool> PasswordMatchesAsync(SqlConnection connection, string loginName, string password, CancellationToken ct = default)
        => Task.FromResult(PasswordMatches(loginName, password));

    /// <summary>Sessions still rolling back after a KILL.</summary>
    public int RollbacksInProgress { get; set; }

    public Task<int> GetRollbacksInProgressAsync(SqlConnection connection, CancellationToken ct = default) => Task.FromResult(RollbacksInProgress);
}
