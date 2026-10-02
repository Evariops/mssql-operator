namespace MsSqlOperator.Services.SqlServer;

public interface ISqlTdeManager
{
    Task<bool> MasterKeyExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task CreateMasterKeyAsync(Microsoft.Data.SqlClient.SqlConnection connection, string password, CancellationToken ct = default);
    Task OpenMasterKeyAsync(Microsoft.Data.SqlClient.SqlConnection connection, string password, CancellationToken ct = default);
    Task<bool> CertificateExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);
    Task CreateCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string subject, string? expiryDate, CancellationToken ct = default);
    Task<string?> GetCertificateThumbprintAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);
    Task EnableDatabaseEncryptionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string certName, string algorithm, CancellationToken ct = default);
    Task DisableDatabaseEncryptionAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task<string?> GetDatabaseEncryptionStateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);

    /// <summary>The encryption state of every database that has an encryption key, in one read of the
    /// server-wide DMV. A database without a key is absent.</summary>
    Task<Dictionary<string, string>> GetDatabaseEncryptionStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task BackupCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string certPath, string keyPath, string encryptionPassword, CancellationToken ct = default);
    Task DropCertificateAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, CancellationToken ct = default);

    // Rotation support
    Task<List<string>> GetUserDatabaseNamesAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task<string?> GetDekProtectorThumbprintAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, CancellationToken ct = default);
    Task ChangeDekProtectorAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string newCertName, CancellationToken ct = default);
    Task RegenerateDekAsync(Microsoft.Data.SqlClient.SqlConnection connection, string database, string algorithm, CancellationToken ct = default);
    Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateWithPasswordAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, string encryptionPassword, CancellationToken ct = default);
    Task RestoreCertificateWithPasswordAsync(Microsoft.Data.SqlClient.SqlConnection connection, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct = default);
}
