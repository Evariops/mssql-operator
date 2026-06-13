namespace MsSqlOperator.Services.S3;

public interface IS3Client
{
    Task PutObjectAsync(S3ConnectionInfo connection, string key, byte[] content, CancellationToken ct = default);
    Task<byte[]> GetObjectAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default);
    Task<bool> ObjectExistsAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default);
    Task<List<string>> ListObjectsAsync(S3ConnectionInfo connection, string prefix, CancellationToken ct = default);
}

public record S3ConnectionInfo(
    string Endpoint,
    string Bucket,
    string Region,
    string AccessKeyId,
    string SecretAccessKey,
    bool ForcePathStyle,
    // HTTP endpoints are refused unless explicitly opted in (test MinIO over HTTP).
    bool AllowInsecureEndpoint = false);
