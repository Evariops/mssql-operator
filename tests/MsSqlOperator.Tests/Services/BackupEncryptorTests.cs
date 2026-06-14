using System.Security.Cryptography;
using MsSqlOperator.Services.Backup;

namespace MsSqlOperator.Tests.Services;

public class BackupEncryptorTests
{
    private readonly BackupEncryptor _encryptor = new();
    private static readonly byte[] Aad = "logical\nv1\nbackups/logical/v1.bundle"u8.ToArray();

    [Fact]
    public void EncryptDecrypt_AesKey_RoundTrip()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "Hello, TDE backup!"u8.ToArray();

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key, Aad);
        var decrypted = _encryptor.DecryptWithAesKey(encrypted, key, Aad);

        Assert.Equivalent(plaintext, decrypted);
    }

    [Fact]
    public void EncryptWithAesKey_EmitsV2Header()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        var encrypted = _encryptor.EncryptWithAesKey("x"u8.ToArray(), key, Aad);

        Assert.Equivalent("MSOB"u8.ToArray(), encrypted.AsSpan(0, 4).ToArray());
        Assert.Equal(2, encrypted[4]);
    }

    [Fact]
    public void DecryptWithAesKey_WrongAad_Throws()
    {
        // Substituting a valid bundle under another identity must fail decryption.
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var encrypted = _encryptor.EncryptWithAesKey("secret"u8.ToArray(), key, Aad);

        var act = () => _encryptor.DecryptWithAesKey(encrypted, key, "other\nv2\nelsewhere"u8.ToArray());
        Assert.Throws<AuthenticationTagMismatchException>(act);
    }

    [Fact]
    public void DecryptWithAesKey_LegacyV1Payload_StillReadable()
    {
        // v1 format: IV(12) + Tag(16) + Ciphertext, no header, no AAD.
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "legacy bundle"u8.ToArray();

        var iv = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        var legacy = new byte[12 + 16 + ciphertext.Length];
        iv.CopyTo(legacy, 0);
        tag.CopyTo(legacy, 12);
        ciphertext.CopyTo(legacy, 28);

        var decrypted = _encryptor.DecryptWithAesKey(legacy, key, Aad);
        Assert.Equivalent(plaintext, decrypted);
    }

    [Fact]
    public void EncryptWithAesKey_DifferentIvEachTime()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = "test"u8.ToArray();

        var enc1 = _encryptor.EncryptWithAesKey(plaintext, key, Aad);
        var enc2 = _encryptor.EncryptWithAesKey(plaintext, key, Aad);

        // IV should differ → ciphertext should differ
        Assert.NotEqual(enc2, enc1);
    }

    [Fact]
    public void EncryptWithAesKey_WrongKey_Throws()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var wrongKey = new byte[32];
        Random.Shared.NextBytes(wrongKey);
        var plaintext = "secret"u8.ToArray();

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key, Aad);

        var act = () => _encryptor.DecryptWithAesKey(encrypted, wrongKey, Aad);
        Assert.Throws<AuthenticationTagMismatchException>(act);
    }

    [Fact]
    public void EncryptWithAesKey_InvalidKeyLength_Throws()
    {
        var key = new byte[16]; // Should be 32
        var plaintext = "test"u8.ToArray();

        var act = () => _encryptor.EncryptWithAesKey(plaintext, key, Aad);
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void EncryptDecrypt_Envelope_RoundTrip()
    {
        using var rsa = RSA.Create(2048);
        var pubKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPublicKeyPem());
        var privKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem());
        var plaintext = "Envelope encrypted backup content"u8.ToArray();

        var encrypted = _encryptor.EncryptWithEnvelope(plaintext, pubKey, Aad);
        var decrypted = _encryptor.DecryptWithEnvelope(encrypted, privKey, Aad);

        Assert.Equivalent(plaintext, decrypted);
    }

    [Fact]
    public void DecryptWithEnvelope_WrongAad_Throws()
    {
        using var rsa = RSA.Create(2048);
        var pubKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPublicKeyPem());
        var privKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem());

        var encrypted = _encryptor.EncryptWithEnvelope("data"u8.ToArray(), pubKey, Aad);

        var act = () => _encryptor.DecryptWithEnvelope(encrypted, privKey, "tampered"u8.ToArray());
        Assert.Throws<AuthenticationTagMismatchException>(act);
    }

    [Fact]
    public void EncryptWithEnvelope_DifferentDekEachTime()
    {
        using var rsa = RSA.Create(2048);
        var pubKey = System.Text.Encoding.UTF8.GetBytes(rsa.ExportRSAPublicKeyPem());
        var plaintext = "test"u8.ToArray();

        var enc1 = _encryptor.EncryptWithEnvelope(plaintext, pubKey, Aad);
        var enc2 = _encryptor.EncryptWithEnvelope(plaintext, pubKey, Aad);

        Assert.NotEqual(enc2, enc1);
    }

    [Fact]
    public void DecryptWithAesKey_TruncatedData_Throws()
    {
        var key = new byte[32];
        var tooShort = new byte[10];

        var act = () => _encryptor.DecryptWithAesKey(tooShort, key, Aad);
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void DecryptWithEnvelope_TruncatedData_Throws()
    {
        var key = new byte[10];

        var act = () => _encryptor.DecryptWithEnvelope(new byte[2], key, Aad);
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void DecryptWithEnvelope_AbsurdKeyLength_ThrowsCleanly()
    {
        // Announced encrypted-key length must be bounded before slicing.
        var data = new byte[64];
        "MSOB"u8.ToArray().CopyTo(data, 0);
        data[4] = 2;
        data[5] = 0x7F; data[6] = 0xFF; data[7] = 0xFF; data[8] = 0xFF; // huge length

        var act = () => _encryptor.DecryptWithEnvelope(data, new byte[16], Aad);
        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("invalid encrypted-key length", ex.Message);
    }

    [Fact]
    public void EncryptDecrypt_AesKey_LargePayload()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = new byte[1024 * 1024]; // 1MB
        Random.Shared.NextBytes(plaintext);

        var encrypted = _encryptor.EncryptWithAesKey(plaintext, key, Aad);
        var decrypted = _encryptor.DecryptWithAesKey(encrypted, key, Aad);

        Assert.Equivalent(plaintext, decrypted);
    }
}
