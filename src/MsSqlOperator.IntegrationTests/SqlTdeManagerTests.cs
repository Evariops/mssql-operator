using FluentAssertions;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection("SqlServer")]
public class SqlTdeManagerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
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
        exists.Should().BeTrue();
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
        exists.Should().BeTrue();

        var thumbprint = await manager.GetCertificateThumbprintAsync(connection, certName);
        thumbprint.Should().NotBeNullOrEmpty();
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
        certBytes.Should().NotBeEmpty();
        keyBytes.Should().NotBeEmpty();

        // The same certificate material cannot exist twice in one instance; drop the source so
        // the re-import (which on a real replica lands in a fresh database) can succeed here.
        await manager.DropCertificateAsync(connection, source);

        var act = async () => await manager.RestoreCertificateWithPasswordAsync(connection, target, certBytes, keyBytes, pwd);
        await act.Should().NotThrowAsync();
        (await manager.CertificateExistsAsync(connection, target)).Should().BeTrue();
    }
}
