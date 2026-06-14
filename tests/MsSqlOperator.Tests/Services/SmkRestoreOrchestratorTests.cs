using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Services.S3;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// The reversibility invariant — a FORCE restore can never make data irrecoverable.
/// The current SMK is safety-backed-up to S3 and verified before any forced restore; if that
/// cannot be done, no FORCE happens.
/// </summary>
public class SmkRestoreOrchestratorTests
{
    private readonly FakeServiceMasterKeyManager _smk = new();
    private readonly FakeS3Client _s3 = new();
    private readonly SmkRestoreOrchestrator _sut;
    private static readonly S3ConnectionInfo Store = new("https://s3", "b", "us-east-1", "ak", "sk", true);

    public SmkRestoreOrchestratorTests()
        => _sut = new SmkRestoreOrchestrator(NullLogger<SmkRestoreOrchestrator>.Instance, _smk, _s3);

    [Fact]
    public async Task PlainRestoreSucceeds_NoForce_NoBackup()
    {
        var key = await _sut.RestoreAsync(new SqlConnection(), "inst", "/tmp/mssqlop_" + new string('a', 32) + ".bak", "pwd", Store, "bkp", CancellationToken.None);

        Assert.Null(key);
        Assert.Empty(_smk.RestoreForcedCalls);
        Assert.Empty(_s3.PutCalls);
    }

    [Fact]
    public async Task PlainRestoreFails_BackupUnavailable_RefusesForce()
    {
        // The plain restore fails, and the safety backup of the CURRENT key cannot be taken →
        // the orchestrator must NOT issue FORCE.
        _smk.RestoreThrows = new InvalidOperationException("decryption failed");
        _smk.BackupThrows = new InvalidOperationException("disk full");

        var act = () => _sut.RestoreAsync(new SqlConnection(), "inst", "/tmp/mssqlop_" + new string('a', 32) + ".bak", "pwd", Store, "bkp", CancellationToken.None);

        await Assert.ThrowsAsync<SmkRestoreBlockedException>(act);
        Assert.Empty(_smk.RestoreForcedCalls);
    }
}
