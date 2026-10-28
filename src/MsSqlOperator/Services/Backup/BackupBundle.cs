using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsSqlOperator.Services.Backup;

public class BackupBundle
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("sqlCertificateName")]
    public string SqlCertificateName { get; set; } = string.Empty;

    [JsonPropertyName("thumbprint")]
    public string Thumbprint { get; set; } = string.Empty;

    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = string.Empty;

    [JsonPropertyName("privateKey")]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The password of the .pvk, inside the encrypted and authenticated bundle — the bundle
    /// restores without the cluster, the case it exists for. An earlier version had moved it out to a Secret of
    /// the cluster; bundles of that time lack it and are completed when the operator can read them.
    /// </summary>
    [JsonPropertyName("privateKeyPassword")]
    public string? PrivateKeyPassword { get; set; }

    /// <summary>Name of the Kubernetes Secret that also holds the private-key password —
    /// informative.</summary>
    [JsonPropertyName("privateKeyPasswordSecretName")]
    public string? PrivateKeyPasswordSecretName { get; set; }

    /// <summary>Key inside that Secret holding the password.</summary>
    [JsonPropertyName("privateKeyPasswordSecretKey")]
    public string? PrivateKeyPasswordSecretKey { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    public byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(this, JsonContext.Default.BackupBundle);

    public static BackupBundle Deserialize(byte[] data) =>
        JsonSerializer.Deserialize(data, JsonContext.Default.BackupBundle)
        ?? throw new InvalidOperationException("Failed to deserialize backup bundle");
}

[JsonSerializable(typeof(BackupBundle))]
internal partial class JsonContext : JsonSerializerContext;
