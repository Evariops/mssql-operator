using MsSqlOperator.Services.S3;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IS3Client"/>: records writes and serves configurable reads.</summary>
public sealed class FakeS3Client : IS3Client
{
    public readonly List<(string Key, byte[] Content)> PutCalls = [];
    public readonly List<string> GetKeys = [];
    public readonly List<string> ExistsKeys = [];
    public readonly List<string> ListPrefixes = [];

    public byte[] GetObjectResult { get; set; } = [];
    public bool ObjectExistsResult { get; set; }
    public List<string> ListObjectsResult { get; set; } = [];
    public Exception? PutThrows { get; set; }

    public Task PutObjectAsync(S3ConnectionInfo connection, string key, byte[] content, CancellationToken ct = default)
    {
        PutCalls.Add((key, content));
        return PutThrows is null ? Task.CompletedTask : Task.FromException(PutThrows);
    }

    public Task<byte[]> GetObjectAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default)
    {
        GetKeys.Add(key);
        return Task.FromResult(GetObjectResult);
    }

    public Task<bool> ObjectExistsAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default)
    {
        ExistsKeys.Add(key);
        return Task.FromResult(ObjectExistsResult);
    }

}
