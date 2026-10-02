using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Services;

/// <summary>The certificate the SQL Server pods present.</summary>
public class SqlServerCertificateTests
{
    private static SqlInstance Instance(TlsSpec? tls) => new()
    {
        Metadata = new V1ObjectMeta { Name = "x", NamespaceProperty = "ns" },
        Spec = new SqlInstanceSpec
        {
            Mode = "SingleNode",
            Version = "2022",
            Edition = "Developer",
            Memory = new MemorySpec { Total = "8Gi" },
            SaPasswordSecretRef = new SecretReference { Name = "sa", Key = "password" },
            Tls = tls,
        },
    };

    [Fact]
    public void MssqlConf_DeclaresTheMountedCertificate()
    {
        var conf = new MssqlConfBuilder().Build(null, null, null, tls: new TlsSpec { SecretName = "sql-tls", ForceEncryption = true });
        Assert.Contains("[network]", conf);
        Assert.Contains("tlscert = /var/opt/mssql/tls/tls.crt", conf);
        Assert.Contains("tlskey = /var/opt/mssql/tls/tls.key", conf);
        Assert.Contains("forceencryption = 1", conf);
        Assert.DoesNotContain("[network]", new MssqlConfBuilder().Build(null, null, null));
    }

    [Fact]
    public void StatefulSet_MountsTheTlsSecret()
    {
        var builder = new StatefulSetBuilder(new AntiAffinityBuilder(), new TopologySpreadBuilder());
        var sts = builder.Build(Instance(new TlsSpec { SecretName = "sql-tls" }), null);

        var volume = sts.Spec.Template.Spec.Volumes.Single(v => v.Name == "mssql-tls");
        Assert.Equal("sql-tls", volume.Secret.SecretName);
        Assert.Equal(["tls.crt", "tls.key"], volume.Secret.Items.Select(i => i.Path));
        var mount = sts.Spec.Template.Spec.Containers[0].VolumeMounts.Single(m => m.Name == "mssql-tls");
        Assert.Equal("/var/opt/mssql/tls", mount.MountPath);
        Assert.True(mount.ReadOnlyProperty);

        Assert.DoesNotContain(builder.Build(Instance(null), null).Spec.Template.Spec.Volumes, v => v.Name == "mssql-tls");
    }

    [Fact]
    public async Task TlsSecret_MissingOrIncomplete_IsReported()
    {
        var client = new FakeKubernetesClient();
        var tls = new TlsSpec { SecretName = "sql-tls" };
        Assert.Contains("not found", await TlsSecret.MissingAsync(client, tls, "ns", CancellationToken.None));

        client.SetGet("sql-tls", "ns", new V1Secret { Data = new Dictionary<string, byte[]> { ["tls.crt"] = [1] } });
        Assert.Contains("lacks", await TlsSecret.MissingAsync(client, tls, "ns", CancellationToken.None));

        client.SetGet("sql-tls", "ns", new V1Secret { Data = new Dictionary<string, byte[]> { ["tls.crt"] = [1], ["tls.key"] = [2] } });
        Assert.Null(await TlsSecret.MissingAsync(client, tls, "ns", CancellationToken.None));
        Assert.Null(await TlsSecret.MissingAsync(client, null, "ns", CancellationToken.None));
    }

    [Fact]
    public void Admission_RequiresTheSecretName()
        => Assert.False(new SqlInstanceValidator().Create(Instance(new TlsSpec()), false).Valid);
}

/// <summary>The S3 endpoint guard.</summary>
public class S3EndpointGuardTests
{
    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("10.96.12.4", false)]
    [InlineData("52.95.110.1", false)]
    public void ForbiddenAddresses(string address, bool forbidden)
        => Assert.Equal(forbidden, MsSqlOperator.Services.S3.S3EndpointGuard.IsForbidden(System.Net.IPAddress.Parse(address)));
}
