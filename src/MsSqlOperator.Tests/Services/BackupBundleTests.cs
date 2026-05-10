using FluentAssertions;
using MsSqlOperator.Services.Backup;

namespace MsSqlOperator.Tests.Services;

public class BackupBundleTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var bundle = new BackupBundle
        {
            LogicalName = "tde-cert",
            Version = "20250109",
            SqlCertificateName = "tde-cert_20250109",
            Thumbprint = "0xABCD1234",
            Certificate = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            PrivateKey = Convert.ToBase64String(new byte[] { 4, 5, 6 }),
            PrivateKeyPassword = "test-password",
            CreatedAt = new DateTime(2025, 1, 9, 12, 0, 0, DateTimeKind.Utc)
        };

        var bytes = bundle.Serialize();
        var deserialized = BackupBundle.Deserialize(bytes);

        deserialized.LogicalName.Should().Be("tde-cert");
        deserialized.Version.Should().Be("20250109");
        deserialized.SqlCertificateName.Should().Be("tde-cert_20250109");
        deserialized.Thumbprint.Should().Be("0xABCD1234");
        deserialized.Certificate.Should().Be(bundle.Certificate);
        deserialized.PrivateKey.Should().Be(bundle.PrivateKey);
        deserialized.PrivateKeyPassword.Should().Be("test-password");
    }
}
