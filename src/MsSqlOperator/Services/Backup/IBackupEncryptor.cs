namespace MsSqlOperator.Services.Backup;

public interface IBackupEncryptor
{
    byte[] EncryptWithAesKey(byte[] plaintext, byte[] aesKey);
    byte[] DecryptWithAesKey(byte[] ciphertext, byte[] aesKey);
    byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem);
    byte[] DecryptWithEnvelope(byte[] ciphertext, byte[] rsaPrivateKeyPem);
}
