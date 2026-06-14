using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IServiceMasterKeyManager"/>: records calls and can be
/// configured to throw per operation (the SMK reversibility tests exercise failure paths).</summary>
public sealed class FakeServiceMasterKeyManager : IServiceMasterKeyManager
{
    public readonly List<(string BackupPath, string Password)> BackupCalls = [];
    public readonly List<(string BackupPath, string Password)> RestoreCalls = [];
    public readonly List<(string BackupPath, string Password)> RestoreForcedCalls = [];

    public Exception? BackupThrows { get; set; }
    public Exception? RestoreThrows { get; set; }
    public Exception? RestoreForcedThrows { get; set; }

    public Task BackupSmkAsync(SqlConnection connection, string backupPath, string encryptionPassword, CancellationToken ct = default)
    {
        BackupCalls.Add((backupPath, encryptionPassword));
        return BackupThrows is null ? Task.CompletedTask : Task.FromException(BackupThrows);
    }

    public Task RestoreSmkAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default)
    {
        RestoreCalls.Add((backupPath, decryptionPassword));
        return RestoreThrows is null ? Task.CompletedTask : Task.FromException(RestoreThrows);
    }

    public Task RestoreSmkForcedAsync(SqlConnection connection, string backupPath, string decryptionPassword, CancellationToken ct = default)
    {
        RestoreForcedCalls.Add((backupPath, decryptionPassword));
        return RestoreForcedThrows is null ? Task.CompletedTask : Task.FromException(RestoreForcedThrows);
    }
}
