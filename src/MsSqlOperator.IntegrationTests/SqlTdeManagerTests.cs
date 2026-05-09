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
}
