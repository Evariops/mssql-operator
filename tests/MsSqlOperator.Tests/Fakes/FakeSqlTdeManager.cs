using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="ISqlTdeManager"/>: serves configurable certificate/DEK
/// state and records the DEK-protector change (the rotation-safety tests assert it is NOT moved).</summary>
public sealed class FakeSqlTdeManager : ISqlTdeManager
{
    public bool MasterKeyExists { get; set; }
    public Func<string, bool> CertificateExists { get; set; } = _ => false;
    public string? Thumbprint { get; set; }
    public string? DekProtectorThumbprint { get; set; }
    public string? EncryptionState { get; set; }
    public List<string> UserDatabases { get; set; } = [];
    public (byte[] CertBytes, byte[] KeyBytes) BackupWithPasswordResult { get; set; } = ([], []);

    public readonly List<(string Database, string NewCert)> ChangeDekProtectorCalls = [];
    public readonly List<string> CreateCertificateCalls = [];
    public readonly List<string> BackupWithPasswordCalls = [];

    public Task<bool> MasterKeyExistsAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(MasterKeyExists);
    public readonly List<string> CreateMasterKeyCalls = [];

    public Task CreateMasterKeyAsync(SqlConnection c, string password, CancellationToken ct = default)
    {
        CreateMasterKeyCalls.Add(password);
        return Task.CompletedTask;
    }
    /// <summary>The passwords the DMK opens with; any, when null.</summary>
    public HashSet<string>? MasterKeyPasswords { get; set; }
    public readonly List<(string Old, string New)> ReprotectCalls = [];

    public Task OpenMasterKeyAsync(SqlConnection c, string password, CancellationToken ct = default)
        => MasterKeyPasswords is null || MasterKeyPasswords.Contains(password)
            ? Task.CompletedTask
            : Task.FromException(new InvalidOperationException("15313: wrong password"));

    public Task ReprotectMasterKeyAsync(SqlConnection c, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        ReprotectCalls.Add((oldPassword, newPassword));
        MasterKeyPasswords?.Remove(oldPassword);
        MasterKeyPasswords?.Add(newPassword);
        return Task.CompletedTask;
    }
    public Task<bool> CertificateExistsAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(CertificateExists(certName));

    public Task CreateCertificateAsync(SqlConnection c, string certName, string subject, string? expiryDate, CancellationToken ct = default)
    {
        CreateCertificateCalls.Add(certName);
        return Task.CompletedTask;
    }

    public Task<string?> GetCertificateThumbprintAsync(SqlConnection c, string certName, CancellationToken ct = default) => Task.FromResult(Thumbprint);
    public readonly List<string> EnableEncryptionCalls = [];

    public Task EnableDatabaseEncryptionAsync(SqlConnection c, string database, string certName, string algorithm, CancellationToken ct = default)
    {
        EnableEncryptionCalls.Add(database);
        return Task.CompletedTask;
    }
    public Task<string?> GetDatabaseEncryptionStateAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(EncryptionState);

    /// <summary>Per-database states for the batched read; empty unless configured.</summary>
    public Dictionary<string, string> EncryptionStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<Dictionary<string, string>> GetDatabaseEncryptionStatesAsync(SqlConnection c, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, string>(EncryptionStates, StringComparer.OrdinalIgnoreCase));
    public Task<List<string>> GetUserDatabaseNamesAsync(SqlConnection c, CancellationToken ct = default) => Task.FromResult(UserDatabases);
    public Task<string?> GetDekProtectorThumbprintAsync(SqlConnection c, string database, CancellationToken ct = default) => Task.FromResult(DekProtectorThumbprint);

    public Task ChangeDekProtectorAsync(SqlConnection c, string database, string newCertName, CancellationToken ct = default)
    {
        ChangeDekProtectorCalls.Add((database, newCertName));
        return Task.CompletedTask;
    }

    public readonly List<string> RegenerateDekCalls = [];

    public Task RegenerateDekAsync(SqlConnection c, string database, string algorithm, CancellationToken ct = default)
    {
        RegenerateDekCalls.Add(database);
        return Task.CompletedTask;
    }
    public Task<(byte[] CertBytes, byte[] KeyBytes)> BackupCertificateWithPasswordAsync(SqlConnection c, string certName, string encryptionPassword, CancellationToken ct = default)
    {
        BackupWithPasswordCalls.Add(certName);
        return Task.FromResult(BackupWithPasswordResult);
    }
    /// <summary>The databases whose DEK regeneration is recorded as owed.</summary>
    public readonly HashSet<string> DekRegenerationDue = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsDekRegenerationDueAsync(SqlConnection c, string database, CancellationToken ct = default)
        => Task.FromResult(DekRegenerationDue.Contains(database));

    public Task SetDekRegenerationDueAsync(SqlConnection c, string database, bool due, CancellationToken ct = default)
    {
        if (due) DekRegenerationDue.Add(database);
        else DekRegenerationDue.Remove(database);
        return Task.CompletedTask;
    }

    public Task RestoreCertificateWithPasswordAsync(SqlConnection c, string certName, byte[] certBytes, byte[] keyBytes, string decryptionPassword, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Hand-written fake for <see cref="MsSqlOperator.Services.Backup.IBackupEncryptor"/> (passthrough).</summary>
public sealed class FakeBackupEncryptor : MsSqlOperator.Services.Backup.IBackupEncryptor
{
    public byte[] EncryptWithAesKey(byte[] plaintext, byte[] aesKey, byte[] aad) => plaintext;
    public byte[] DecryptWithAesKey(byte[] ciphertext, byte[] aesKey, byte[] aad, bool allowLegacyV1 = false) => ciphertext;
    public byte[] EncryptWithEnvelope(byte[] plaintext, byte[] rsaPublicKeyPem, byte[] aad) => plaintext;
    public byte[] DecryptWithEnvelope(byte[] ciphertext, byte[] rsaPrivateKeyPem, byte[] aad, bool allowLegacyV1 = false) => ciphertext;
}
