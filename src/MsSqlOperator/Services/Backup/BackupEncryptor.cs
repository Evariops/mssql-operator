using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MsSqlOperator.Services.Backup;

public class BackupEncryptor : IBackupEncryptor
{
    private const int IvLength = 12;
    private const int TagLength = 16;

    public byte[] EncryptWithAesKey(byte[] plaintext, byte[] aesKey)
    {
        if (aesKey.Length != 32)
            throw new ArgumentException("AES key must be 32 bytes (AES-256)");

        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(aesKey, TagLength);
        aes.Encrypt(iv, plaintext, ciphertext, tag);

        // Format: IV(12) + Tag(16) + Ciphertext
        var result = new byte[IvLength + TagLength + ciphertext.Length];
        iv.CopyTo(result, 0);
        tag.CopyTo(result, IvLength);
        ciphertext.CopyTo(result, IvLength + TagLength);
        return result;
    }

    public byte[] DecryptWithAesKey(byte[] data, byte[] aesKey)
    {
        if (aesKey.Length != 32)
            throw new ArgumentException("AES key must be 32 bytes (AES-256)");
        if (data.Length < IvLength + TagLength)
            throw new ArgumentException("Data too short to contain IV and tag");

        var iv = data.AsSpan(0, IvLength);
        var tag = data.AsSpan(IvLength, TagLength);
        var ciphertext = data.AsSpan(IvLength + TagLength);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(aesKey, TagLength);
        aes.Decrypt(iv, ciphertext, tag, plaintext);
        return plaintext;
    }

    public byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem)
    {
        // Generate random DEK
        var dek = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        // Encrypt data with DEK
        using var aes = new AesGcm(dek, TagLength);
        aes.Encrypt(iv, plaintext, ciphertext, tag);

        // Encrypt DEK with RSA public key
        using var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(rsaPublicKeyPem));
        var encryptedDek = rsa.Encrypt(dek, RSAEncryptionPadding.OaepSHA256);

        // Format: EncKeyLen(4 BE) + EncryptedDEK + IV(12) + Tag(16) + Ciphertext
        var result = new byte[4 + encryptedDek.Length + IvLength + TagLength + ciphertext.Length];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), encryptedDek.Length);
        encryptedDek.CopyTo(result, 4);
        iv.CopyTo(result, 4 + encryptedDek.Length);
        tag.CopyTo(result, 4 + encryptedDek.Length + IvLength);
        ciphertext.CopyTo(result, 4 + encryptedDek.Length + IvLength + TagLength);

        // Clear DEK from memory
        CryptographicOperations.ZeroMemory(dek);
        return result;
    }

    public byte[] DecryptWithEnvelope(byte[] data, byte[] rsaPrivateKeyPem)
    {
        if (data.Length < 4)
            throw new ArgumentException("Data too short to contain envelope header");

        var encKeyLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
        if (data.Length < 4 + encKeyLen + IvLength + TagLength)
            throw new ArgumentException("Data too short to contain envelope");

        var encryptedDek = data.AsSpan(4, encKeyLen);
        var offset = 4 + encKeyLen;
        var iv = data.AsSpan(offset, IvLength);
        var tag = data.AsSpan(offset + IvLength, TagLength);
        var ciphertext = data.AsSpan(offset + IvLength + TagLength);

        // Decrypt DEK with RSA private key
        using var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(rsaPrivateKeyPem));
        var dek = rsa.Decrypt(encryptedDek.ToArray(), RSAEncryptionPadding.OaepSHA256);

        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(dek, TagLength);
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
