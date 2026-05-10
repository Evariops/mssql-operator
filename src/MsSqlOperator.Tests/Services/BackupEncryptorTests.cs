using FluentAssertions;
using MsSqlOperator.Services.Backup;

namespace MsSqlOperator.Tests.Services;

public class BackupEncryptorTests
{
    private readonly BackupEncryptor _encryptor = new();

    [Fact]
    public void EncryptDecrypt_AesKey_RoundTrip()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "Hello, TDE backup!"u8.ToArray();

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key);
        var decrypted = _encryptor.DecryptWithAesKey(encrypted, key);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public void EncryptWithAesKey_DifferentIvEachTime()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "test"u8.ToArray();

        var enc1 = _encryptor.EncryptWithAesKey(plaintext, key);
        var enc2 = _encryptor.EncryptWithAesKey(plaintext, key);

        // IV should differ → ciphertext should differ
        enc1.Should().NotBeEquivalentTo(enc2);
    }

    [Fact]
    public void EncryptWithAesKey_WrongKey_Throws()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var wrongKey = new byte[32];
        Random.Shared.NextBytes(wrongKey);
        var plaintext = "secret"u8.ToArray();

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key);

        var act = () => _encryptor.DecryptWithAesKey(encrypted, wrongKey);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EncryptWithAesKey_InvalidKeyLength_Throws()
    {
        var key = new byte[16]; // Should be 32
        var plaintext = "test"u8.ToArray();

        var act = () => _encryptor.EncryptWithAesKey(plaintext, key);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncryptDecrypt_Envelope_RoundTrip()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pubKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPublicKeyPem());
        var privKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem());
        var plaintext = "Envelope encrypted backup content"u8.ToArray();

        var encrypted = _encryptor.EncryptWithEnvelope(plaintext, pubKey);
        var decrypted = _encryptor.DecryptWithEnvelope(encrypted, privKey);

        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public void EncryptWithEnvelope_DifferentDekEachTime()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pubKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPublicKeyPem());
        var plaintext = "test"u8.ToArray();

        var enc1 = _encryptor.EncryptWithEnvelope(plaintext, pubKey);
        var enc2 = _encryptor.EncryptWithEnvelope(plaintext, pubKey);

        enc1.Should().NotBeEquivalentTo(enc2);
    }

    [Fact]
    public void DecryptWithAesKey_TruncatedData_Throws()
    {
        var key = new byte[32];
        var tooShort = new byte[10];

        var act = () => _encryptor.DecryptWithAesKey(tooShort, key);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DecryptWithEnvelope_TruncatedData_Throws()
    {
        var key = new byte[10];

        var act = () => _encryptor.DecryptWithEnvelope(new byte[2], key);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncryptDecrypt_AesKey_LargePayload()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = new byte[1024 * 1024]; // 1MB
        Random.Shared.NextBytes(plaintext);

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key);
        var decrypted = _encryptor.DecryptWithAesKey(encrypted, key);

        decrypted.Should().BeEquivalentTo(plaintext);
    }
}
