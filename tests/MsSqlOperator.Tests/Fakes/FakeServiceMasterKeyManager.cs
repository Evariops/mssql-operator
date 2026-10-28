using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IServiceMasterKeyManager"/>: records calls and can be
/// configured to throw per operation (the SMK reversibility tests exercise failure paths).</summary>
public sealed class FakeServiceMasterKeyManager : IServiceMasterKeyManager
{
    public readonly List<(string Host, string Password)> BackupCalls = [];
    public readonly List<(string BackupPath, string Password)> RestoreCalls = [];
    public readonly List<(string BackupPath, string Password)> RestoreForcedCalls = [];

    public Exception? BackupThrows { get; set; }
    public Exception? RestoreThrows { get; set; }
    public Exception? RestoreForcedThrows { get; set; }

    /// <summary>When the service master key was last modified: drives the staleness
    /// check that refreshes an SMK backup which no longer matches the live key.</summary>
    public DateTime? SmkModifyDate { get; set; }

    public Task<DateTime?> GetSmkModifyDateAsync(SqlConnection connection, CancellationToken ct = default)
        => Task.FromResult(SmkModifyDate);

    /// <summary>What a backup reads back from the server.</summary>
    public byte[]? BackupBytes { get; set; } = [0x53, 0x4d, 0x4b];

    public Task<byte[]?> BackupSmkAsync(SqlConnection connection, string encryptionPassword, CancellationToken ct = default)
    {
        BackupCalls.Add((connection.DataSource, encryptionPassword));
        return BackupThrows is null ? Task.FromResult(BackupBytes) : Task.FromException<byte[]?>(BackupThrows);
    }

}
