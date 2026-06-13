using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MsSqlOperator.Services.Backup;

/// <summary>
/// Encrypts backup bundles for S3. Format v2: a 5-byte header (magic "MSOB" +
/// format version) prefixes the payload and is authenticated, together with the caller's
/// logical identity (AAD), by AES-GCM — a bundle substituted by another valid one, or
/// replayed under a different object key, fails decryption. Legacy v1 payloads (no magic)
/// remain readable during the transition.
/// </summary>
public class BackupEncryptor : IBackupEncryptor
{
    private const int IvLength = 12;
    private const int TagLength = 16;
    private const byte FormatVersion = 2;
    private static ReadOnlySpan<byte> Magic => "MSOB"u8;
    private const int HeaderLength = 5; // Magic(4) + Version(1)

    private static byte[] BuildHeader()
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        return header;
    }

    private static byte[] BuildAssociatedData(byte[] header, byte[] aad)
        => [.. header, .. aad];

    private static bool HasMagic(byte[] data)
        => data.Length >= HeaderLength && data.AsSpan(0, 4).SequenceEqual(Magic);

    public byte[] EncryptWithAesKey(byte[] plaintext, byte[] aesKey, byte[] aad)
    {
        if (aesKey.Length != 32)
            throw new ArgumentException("AES key must be 32 bytes (AES-256)");

        var header = BuildHeader();
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(aesKey, TagLength);
        aes.Encrypt(iv, plaintext, ciphertext, tag, BuildAssociatedData(header, aad));

        // Format v2: Header(5) + IV(12) + Tag(16) + Ciphertext
        var result = new byte[HeaderLength + IvLength + TagLength + ciphertext.Length];
        header.CopyTo(result, 0);
        iv.CopyTo(result, HeaderLength);
        tag.CopyTo(result, HeaderLength + IvLength);
        ciphertext.CopyTo(result, HeaderLength + IvLength + TagLength);
        return result;
    }

    public byte[] DecryptWithAesKey(byte[] data, byte[] aesKey, byte[] aad)
    {
        if (aesKey.Length != 32)
            throw new ArgumentException("AES key must be 32 bytes (AES-256)");

        if (!HasMagic(data))
            return DecryptAesLegacyV1(data, aesKey);

        if (data[4] != FormatVersion)
            throw new CryptographicException($"Unsupported backup bundle format version {data[4]}.");
        if (data.Length < HeaderLength + IvLength + TagLength)
            throw new ArgumentException("Data too short to contain IV and tag");

        var header = data.AsSpan(0, HeaderLength).ToArray();
        var iv = data.AsSpan(HeaderLength, IvLength);
        var tag = data.AsSpan(HeaderLength + IvLength, TagLength);
        var ciphertext = data.AsSpan(HeaderLength + IvLength + TagLength);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(aesKey, TagLength);
        aes.Decrypt(iv, ciphertext, tag, plaintext, BuildAssociatedData(header, aad));
        return plaintext;
    }

    /// <summary>v1: IV(12) + Tag(16) + Ciphertext, no header, no AAD. Read-only support.</summary>
    private static byte[] DecryptAesLegacyV1(byte[] data, byte[] aesKey)
    {
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

    public byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem, byte[] aad)
    {
        var header = BuildHeader();
        var dek = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        try
        {
            using var aes = new AesGcm(dek, TagLength);
            aes.Encrypt(iv, plaintext, ciphertext, tag, BuildAssociatedData(header, aad));

            using var rsa = RSA.Create();
            rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(rsaPublicKeyPem));
            var encryptedDek = rsa.Encrypt(dek, RSAEncryptionPadding.OaepSHA256);

            // Format v2: Header(5) + EncKeyLen(4 BE) + EncryptedDEK + IV(12) + Tag(16) + Ciphertext
            var result = new byte[HeaderLength + 4 + encryptedDek.Length + IvLength + TagLength + ciphertext.Length];
            header.CopyTo(result, 0);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(HeaderLength, 4), encryptedDek.Length);
            encryptedDek.CopyTo(result, HeaderLength + 4);
            iv.CopyTo(result, HeaderLength + 4 + encryptedDek.Length);
            tag.CopyTo(result, HeaderLength + 4 + encryptedDek.Length + IvLength);
            ciphertext.CopyTo(result, HeaderLength + 4 + encryptedDek.Length + IvLength + TagLength);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public byte[] DecryptWithEnvelope(byte[] data, byte[] rsaPrivateKeyPem, byte[] aad)
    {
        var headerLength = HasMagic(data) ? HeaderLength : 0;
        var v2 = headerLength > 0;
        if (v2 && data[4] != FormatVersion)
            throw new CryptographicException($"Unsupported backup bundle format version {data[4]}.");

        if (data.Length < headerLength + 4)
            throw new ArgumentException("Data too short to contain envelope header");

        var encKeyLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(headerLength, 4));
        // Bound-check the announced key length before slicing.
        if (encKeyLen <= 0 || encKeyLen > data.Length - (headerLength + 4 + IvLength + TagLength))
            throw new ArgumentException($"Envelope announces an invalid encrypted-key length ({encKeyLen}).");

        var encryptedDek = data.AsSpan(headerLength + 4, encKeyLen);
        var offset = headerLength + 4 + encKeyLen;
        var iv = data.AsSpan(offset, IvLength);
        var tag = data.AsSpan(offset + IvLength, TagLength);
        var ciphertext = data.AsSpan(offset + IvLength + TagLength);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(rsaPrivateKeyPem));
        var dek = rsa.Decrypt(encryptedDek.ToArray(), RSAEncryptionPadding.OaepSHA256);

        try
        {
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(dek, TagLength);
            if (v2)
                aes.Decrypt(iv, ciphertext, tag, plaintext, BuildAssociatedData(data.AsSpan(0, HeaderLength).ToArray(), aad));
            else
                aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
