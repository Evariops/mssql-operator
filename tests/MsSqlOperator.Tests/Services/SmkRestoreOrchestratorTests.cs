using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Services.S3;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// The reversibility invariant — a FORCE restore can never make data irrecoverable.
/// The current SMK is safety-backed-up to S3 and verified before any forced restore; if that
/// cannot be done, no FORCE happens.
/// </summary>
public class SmkRestoreOrchestratorTests
{
    private readonly Mock<IServiceMasterKeyManager> _smk = new();
    private readonly Mock<IS3Client> _s3 = new();
    private readonly SmkRestoreOrchestrator _sut;
    private static readonly S3ConnectionInfo Store = new("https://s3", "b", "us-east-1", "ak", "sk", true);

    public SmkRestoreOrchestratorTests()
        => _sut = new SmkRestoreOrchestrator(Mock.Of<ILogger<SmkRestoreOrchestrator>>(), _smk.Object, _s3.Object);

    [Fact]
    public async Task PlainRestoreSucceeds_NoForce_NoBackup()
    {
        _smk.Setup(m => m.RestoreSmkAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var key = await _sut.RestoreAsync(new SqlConnection(), "inst", "/tmp/mssqlop_" + new string('a', 32) + ".bak", "pwd", Store, "bkp", CancellationToken.None);

        Assert.Null(key);
        _smk.Verify(m => m.RestoreSmkForcedAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _s3.Verify(s => s.PutObjectAsync(It.IsAny<S3ConnectionInfo>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlainRestoreFails_BackupUnavailable_RefusesForce()
    {
        // The plain restore fails, and the safety backup of the CURRENT key cannot be taken →
        // the orchestrator must NOT issue FORCE.
        _smk.Setup(m => m.RestoreSmkAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("decryption failed"));
        _smk.Setup(m => m.BackupSmkAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk full"));

        var act = () => _sut.RestoreAsync(new SqlConnection(), "inst", "/tmp/mssqlop_" + new string('a', 32) + ".bak", "pwd", Store, "bkp", CancellationToken.None);

        await Assert.ThrowsAsync<SmkRestoreBlockedException>(act);
        _smk.Verify(m => m.RestoreSmkForcedAsync(It.IsAny<SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
