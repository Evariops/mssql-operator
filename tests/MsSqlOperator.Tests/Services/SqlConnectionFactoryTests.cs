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
        Assert.Contains("Encrypt=True", cs); // Mandatory
        Assert.Contains("Trust Server Certificate=True", cs);
    }

    [Theory]
    [InlineData("Optional", "Encrypt=False")]
    [InlineData("Mandatory", "Encrypt=True")]
    [InlineData("Strict", "Encrypt=Strict")]
    public void Encrypt_Mode_IsMapped(string mode, string expected)
    {
        Assert.Contains(expected, Build(new SqlConnectionOptions { Encrypt = mode }));
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

        Assert.Contains("Trust Server Certificate=False", cs);
        Assert.Contains("Host Name In Certificate=*.mssql.svc.cluster.local", cs);
        Assert.Contains("Server Certificate=/etc/tls/ca.pem", cs);
    }

    [Fact]
    public void OptionalTlsKnobs_Omitted_When_NotConfigured()
    {
        var cs = Build(new SqlConnectionOptions());
        Assert.DoesNotContain("Host Name In Certificate", cs);
        // Note: "Trust Server Certificate" legitimately appears, so assert the standalone key is absent.
        Assert.DoesNotContain("Server Certificate=/", cs);
    }

    [Fact]
    public void UserId_DefaultsToSa()
    {
        Assert.Contains("User ID=sa", Build(new SqlConnectionOptions()));
    }

    [Fact]
    public void UserId_HonoursDeployerLogin()
    {
        // The stored-procedure deploy path opens its connection as the least-privilege
        // db_ddladmin login rather than sa.
        var cs = new SqlConnectionFactory(Options.Create(new SqlConnectionOptions()))
            .BuildConnectionString("sql-0", 1433, "p@ss", "master", user: "mssqlop_proc_deployer");
        Assert.Contains("User ID=mssqlop_proc_deployer", cs);
        Assert.DoesNotContain("User ID=sa", cs);
    }
}
