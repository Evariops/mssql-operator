using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

/// <summary>Where the SMK backup file goes, on a real engine — one without the
/// in-memory key directory, like a pod created by an earlier operator version.</summary>
[Collection(SqlServerCollection.Name)]
public class ServiceMasterKeyManagerTests(SqlServerFixture fixture)
{
    private static readonly string Password = $"Smk-{Guid.NewGuid():N}-A1!";

    [Fact]
    public async Task DirectoryDetection_SeesAnExistingDirectory()
    {
        // dm_os_file_exists answers in tinyint; read as a bit-to-bool it was never true, and the
        // key backups of every pod went to /tmp.
        await using var connection = await fixture.CreateConnectionAsync();

        Assert.True(await SqlTempPaths.IsDirectoryAsync(connection, "/tmp", CancellationToken.None));
        Assert.False(await SqlTempPaths.IsDirectoryAsync(connection, SqlTempPaths.KeyDirectory, CancellationToken.None));
    }

    [Fact]
    public async Task Backup_WithoutTheKeyVolume_FallsBackToTmp()
    {
        await using var connection = await fixture.CreateConnectionAsync();

        var bytes = await new ServiceMasterKeyManager().BackupSmkAsync(connection, Password, keyVolumeMounted: false);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Backup_WhenThePodMountsTheVolume_ButTheServerDoesNotSeeIt_IsRefused()
    {
        // On a pod that has the volume, a server that does not (yet) see the directory must
        // not send the key to the node's disk.
        await using var connection = await fixture.CreateConnectionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ServiceMasterKeyManager().BackupSmkAsync(connection, Password, keyVolumeMounted: true));
    }
}
