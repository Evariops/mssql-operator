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
    /// Legacy v1 field: old bundles embedded the .pvk password. New bundles never
    /// set it — they reference the Kubernetes Secret holding the password instead. Kept
    /// nullable so old bundles still deserialize during the transition.
    /// </summary>
    [JsonPropertyName("privateKeyPassword")]
    public string? PrivateKeyPassword { get; set; }

    /// <summary>Name of the Kubernetes Secret holding the private-key password.</summary>
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
