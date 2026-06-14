using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SqlTdeManagerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task MasterKey_CreateAndCheck()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlTdeManager();

        if (!await manager.MasterKeyExistsAsync(connection))
        {
            await manager.CreateMasterKeyAsync(connection, "M@sterK3y!");
        }

        var exists = await manager.MasterKeyExistsAsync(connection);
        Assert.True(exists);
    }

    [Fact]
    public async Task Certificate_CreateAndGetThumbprint()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlTdeManager();

        // Ensure master key
        if (!await manager.MasterKeyExistsAsync(connection))
        {
            await manager.CreateMasterKeyAsync(connection, "M@sterK3y!");
        }

        var certName = $"TestCert_{Guid.NewGuid():N}"[..30];
        await manager.CreateCertificateAsync(connection, certName, "Test Certificate", null);

        var exists = await manager.CertificateExistsAsync(connection, certName);
        Assert.True(exists);

        var thumbprint = await manager.GetCertificateThumbprintAsync(connection, certName);
        Assert.False(string.IsNullOrEmpty(thumbprint));
    }

    [Fact]
    public async Task Certificate_BackupAndRestoreFromBinary_RoundTrips()
    {
        // Regression: the controller exports a certificate to binary and re-imports it via
        // CREATE CERTIFICATE FROM BINARY. That used query parameters for the bytes and
        // password, which T-SQL rejects ("Incorrect syntax near '@cert'"). The round-trip
        // must succeed with binary (0x...) literals and an escaped password literal.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlTdeManager();

        if (!await manager.MasterKeyExistsAsync(connection))
            await manager.CreateMasterKeyAsync(connection, "M@sterK3y!");

        var source = $"TdeSrc_{Guid.NewGuid():N}"[..28];
        var target = $"TdeDst_{Guid.NewGuid():N}"[..28];
        const string pwd = "C3rtP@ss'word!";

        await manager.CreateCertificateAsync(connection, source, "RoundTrip Cert", null);
        var (certBytes, keyBytes) = await manager.BackupCertificateWithPasswordAsync(connection, source, pwd);
        Assert.NotEmpty(certBytes);
        Assert.NotEmpty(keyBytes);

        // The same certificate material cannot exist twice in one instance; drop the source so
        // the re-import (which on a real replica lands in a fresh database) can succeed here.
        await manager.DropCertificateAsync(connection, source);

        var act = async () => await manager.RestoreCertificateWithPasswordAsync(connection, target, certBytes, keyBytes, pwd);
        await act();
        Assert.True((await manager.CertificateExistsAsync(connection, target)));
    }

    [Fact]
    public async Task ImportCertificate_OnFreshSession_RequiresOpenMasterKey()
    {
        // Regression: importing a certificate's private key re-encrypts it with the database
        // master key, which must be OPEN in the session. A DMK created in an earlier session is
        // not auto-opened for this on SQL Server for Linux, so the operator opens it explicitly.
        // This reproduces the cluster failure ("Please ... open the master key ...") by doing
        // the import on a brand-new connection.
        const string dmkPwd = "M@sterK3y!";
        const string certPwd = "C3rtP@ss'word!";
        var source = $"TdeXsrc_{Guid.NewGuid():N}"[..28];
        var target = $"TdeXdst_{Guid.NewGuid():N}"[..28];
        var manager = new SqlTdeManager();

        byte[] certBytes, keyBytes;
        await using (var setup = await fixture.CreateConnectionAsync())
        {
            if (!await manager.MasterKeyExistsAsync(setup))
                await manager.CreateMasterKeyAsync(setup, dmkPwd);
            await manager.OpenMasterKeyAsync(setup, dmkPwd);
            await manager.CreateCertificateAsync(setup, source, "Xsession Cert", null);
            (certBytes, keyBytes) = await manager.BackupCertificateWithPasswordAsync(setup, source, certPwd);
            await manager.DropCertificateAsync(setup, source);
        }

        // Fresh session: the DMK is not auto-open here. The operator must open it first.
        await using var fresh = await fixture.CreateConnectionAsync();
        await manager.OpenMasterKeyAsync(fresh, dmkPwd);
        var act = async () => await manager.RestoreCertificateWithPasswordAsync(fresh, target, certBytes, keyBytes, certPwd);
        await act();
        Assert.True((await manager.CertificateExistsAsync(fresh, target)));
    }
}
