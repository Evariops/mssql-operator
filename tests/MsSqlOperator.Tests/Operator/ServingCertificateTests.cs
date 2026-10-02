using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Operator;

public class ServingCertificateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("serving-cert").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(Path.Combine(_directory, "tls.crt"), cert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "tls.key"), key.ExportPkcs8PrivateKeyPem());
    }

    //
    [Fact]
    public void RenewedSecret_IsServedAfterTheNextCheck()
    {
        WriteCertificate("first");
        var time = new FakeTimeProvider();
        var serving = new ServingCertificate(_directory, time);
        Assert.Equal("CN=first", serving.Current.Subject);
        Assert.True(serving.Current.HasPrivateKey);

        WriteCertificate("second");
        Assert.Equal("CN=first", serving.Current.Subject); // within the check interval

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("CN=second", serving.Current.Subject);
    }

    [Fact]
    public void HalfWrittenSecret_KeepsThePreviousCertificate()
    {
        WriteCertificate("first");
        var time = new FakeTimeProvider();
        var serving = new ServingCertificate(_directory, time);

        File.WriteAllText(Path.Combine(_directory, "tls.key"), "garbage");
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("CN=first", serving.Current.Subject);
    }

    [Fact]
    public void MissingCertificate_FailsAtStartup()
        => Assert.ThrowsAny<IOException>(() => new ServingCertificate(Path.Combine(_directory, "absent"), TimeProvider.System));
}
