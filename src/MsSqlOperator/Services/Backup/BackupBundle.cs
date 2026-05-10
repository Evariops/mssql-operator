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

    [JsonPropertyName("privateKeyPassword")]
    public string PrivateKeyPassword { get; set; } = string.Empty;

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
