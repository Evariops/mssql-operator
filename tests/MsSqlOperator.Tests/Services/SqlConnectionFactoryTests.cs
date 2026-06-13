using FluentAssertions;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.ConnectionFactory;

namespace MsSqlOperator.Tests.Services;

public class SqlConnectionFactoryTests
{
    private static string Build(SqlConnectionOptions opts) =>
        new SqlConnectionFactory(Options.Create(opts)).BuildConnectionString("sql-0", 1433, "p@ss", "master");

    [Fact]
    public void Default_Is_Encrypted_And_TrustsSelfSigned()
    {
        var cs = Build(new SqlConnectionOptions());
        cs.Should().Contain("Encrypt=True"); // Mandatory
        cs.Should().Contain("Trust Server Certificate=True");
    }

    [Theory]
    [InlineData("Optional", "Encrypt=False")]
    [InlineData("Mandatory", "Encrypt=True")]
    [InlineData("Strict", "Encrypt=Strict")]
    public void Encrypt_Mode_IsMapped(string mode, string expected)
    {
        Build(new SqlConnectionOptions { Encrypt = mode }).Should().Contain(expected);
    }

    [Fact]
    public void FullValidation_AppliesHostNameAndServerCertificate()
    {
        var cs = Build(new SqlConnectionOptions
        {
            Encrypt = "Strict",
            TrustServerCertificate = false,
            HostNameInCertificate = "*.mssql.svc.cluster.local",
            ServerCertificate = "/etc/tls/ca.pem"
        });

        cs.Should().Contain("Trust Server Certificate=False");
        cs.Should().Contain("Host Name In Certificate=*.mssql.svc.cluster.local");
        cs.Should().Contain("Server Certificate=/etc/tls/ca.pem");
    }

    [Fact]
    public void OptionalTlsKnobs_Omitted_When_NotConfigured()
    {
        var cs = Build(new SqlConnectionOptions());
        cs.Should().NotContain("Host Name In Certificate");
        // Note: "Trust Server Certificate" legitimately appears, so assert the standalone key is absent.
        cs.Should().NotContain("Server Certificate=/");
    }

    [Fact]
    public void UserId_DefaultsToSa()
    {
        Build(new SqlConnectionOptions()).Should().Contain("User ID=sa");
    }

    [Fact]
    public void UserId_HonoursDeployerLogin()
    {
        // The stored-procedure deploy path opens its connection as the least-privilege
        // db_ddladmin login rather than sa.
        var cs = new SqlConnectionFactory(Options.Create(new SqlConnectionOptions()))
            .BuildConnectionString("sql-0", 1433, "p@ss", "master", user: "mssqlop_proc_deployer");
        cs.Should().Contain("User ID=mssqlop_proc_deployer");
        cs.Should().NotContain("User ID=sa");
    }
}
