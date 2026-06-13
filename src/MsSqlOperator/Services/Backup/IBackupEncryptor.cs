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
    byte[] DecryptWithAesKey(byte[] ciphertext, byte[] aesKey, byte[] aad);
    byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem, byte[] aad);
    byte[] DecryptWithEnvelope(byte[] ciphertext, byte[] rsaPrivateKeyPem, byte[] aad);
}
