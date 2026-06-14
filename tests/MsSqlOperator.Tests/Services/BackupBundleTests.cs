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

        Assert.Equal("tde-cert", deserialized.LogicalName);
        Assert.Equal("20250109", deserialized.Version);
        Assert.Equal("tde-cert_20250109", deserialized.SqlCertificateName);
        Assert.Equal("0xABCD1234", deserialized.Thumbprint);
        Assert.Equal(bundle.Certificate, deserialized.Certificate);
        Assert.Equal(bundle.PrivateKey, deserialized.PrivateKey);
        Assert.Equal("test-password", deserialized.PrivateKeyPassword);
    }
}
