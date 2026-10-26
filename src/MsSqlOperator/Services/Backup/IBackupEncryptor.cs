namespace MsSqlOperator.Services.Backup;

/// <summary>
/// Backup bundle encryption. The <c>aad</c> parameter is the bundle's logical
/// identity (logical name, version, S3 object key): it is authenticated alongside the
/// ciphertext, so a bundle substituted by another valid one or replayed under a different
/// object key fails decryption.
/// </summary>
public interface IBackupEncryptor
{
    byte[] EncryptWithAesKey(byte[] plaintext, byte[] aesKey, byte[] aad);
    /// <summary>Decrypts a bundle. A legacy v1 bundle (no header, nothing authenticated beyond the
    /// ciphertext) is refused unless <paramref name="allowLegacyV1"/> — accepting it silently
    /// let a substituted or replayed bundle be read as genuine.</summary>
    byte[] DecryptWithAesKey(byte[] ciphertext, byte[] aesKey, byte[] aad, bool allowLegacyV1 = false);
    byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem, byte[] aad);
    /// <inheritdoc cref="DecryptWithAesKey"/>
    byte[] DecryptWithEnvelope(byte[] ciphertext, byte[] rsaPrivateKeyPem, byte[] aad, bool allowLegacyV1 = false);
}
