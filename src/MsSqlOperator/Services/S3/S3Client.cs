using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MsSqlOperator.Services.S3;

public class S3Client(HttpClient httpClient, TimeProvider? timeProvider = null) : IS3Client
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task PutObjectAsync(S3ConnectionInfo connection, string key, byte[] content, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Put, connection, key, content);
        SignRequest(request, content, connection);

        // The response holds a pooled connection until it is disposed.
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> GetObjectAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Get, connection, key);
        SignRequest(request, [], connection);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> ObjectExistsAsync(S3ConnectionInfo connection, string key, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Head, connection, key);
        SignRequest(request, [], connection);

        using var response = await httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return true;
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        // Any other status (403, 500, etc.) is an unexpected error — do not treat as "exists"
        response.EnsureSuccessStatusCode();
        return false; // unreachable
    }

    /// <summary>
    /// The SigV4 canonical query string: parameters sorted by name, each name and value
    /// URI-encoded with RFC-3986 rules. An unsorted string produces a signature the server rejects.
    /// </summary>
    internal static string BuildCanonicalQueryString(string rawQuery)
    {
        var query = rawQuery.TrimStart('?');
        if (query.Length == 0)
            return string.Empty;

        var parameters = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var idx = part.IndexOf('=');
                var name = idx < 0 ? part : part[..idx];
                var value = idx < 0 ? string.Empty : part[(idx + 1)..];
                // Values arrive already escaped by the caller; normalise by decoding then re-encoding
                // so the canonical form is exactly what SigV4 expects.
                return (Name: Uri.EscapeDataString(Uri.UnescapeDataString(name)),
                        Value: Uri.EscapeDataString(Uri.UnescapeDataString(value)));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join('&', parameters.Select(p => $"{p.Name}={p.Value}"));
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, S3ConnectionInfo conn, string key,
        byte[]? content = null, string? queryString = null)
    {
        var uri = BuildUri(conn, key, queryString);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Host = uri.Host;

        if (content is { Length: > 0 })
            request.Content = new ByteArrayContent(content);

        return request;
    }

    private static Uri BuildUri(S3ConnectionInfo conn, string key, string? queryString = null)
    {
        var baseUri = conn.Endpoint.TrimEnd('/');

        // Refuse plaintext endpoints unless explicitly opted in.
        if (baseUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !conn.AllowInsecureEndpoint)
            throw new InvalidOperationException(
                $"S3 endpoint '{conn.Endpoint}' is not HTTPS. Set allowInsecureEndpoint=true only for a trusted test store (e.g. in-cluster MinIO).");

        // Encode each key segment (RFC3986) and reject traversal — logicalName from
        // the CR flows into the key.
        var encodedKey = EncodeKey(key);
        string url;

        if (conn.ForcePathStyle)
            url = $"{baseUri}/{conn.Bucket}/{encodedKey}".TrimEnd('/');
        else
        {
            var endpoint = new Uri(baseUri);
            url = $"{endpoint.Scheme}://{conn.Bucket}.{endpoint.Host}:{endpoint.Port}/{encodedKey}".TrimEnd('/');
        }

        if (!string.IsNullOrEmpty(queryString))
            url += $"?{queryString}";

        return new Uri(url);
    }

    /// <summary>RFC3986 per-segment encoding. Slashes between segments stay literal;
    /// everything else inside a segment is percent-encoded. Path traversal is rejected.</summary>
    private static string EncodeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var segments = key.Split('/');
        if (segments.Any(s => s is "." or ".."))
            throw new InvalidOperationException($"S3 object key '{key}' contains a path traversal segment.");
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private void SignRequest(HttpRequestMessage request, byte[] payload, S3ConnectionInfo conn)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var payloadHash = HashSha256(payload);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var uri = request.RequestUri!;
        var host = uri.Host + (uri.IsDefaultPort ? "" : $":{uri.Port}");

        // Canonical request.
        // SigV4 requires the canonical query string to be sorted by parameter NAME (and
        // each name/value URI-encoded). Passing the raw query through worked by luck for the single-
        // parameter requests, but the paginated listing sends `list-type=2&prefix=…&continuation-token=…`
        // — already out of order — so every page past the first was signed wrong and rejected with 403.
        // Certificates beyond the first 1000 objects were therefore invisible to the operator.
        var canonicalUri = uri.AbsolutePath;
        var canonicalQueryString = BuildCanonicalQueryString(uri.Query);
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            canonicalUri,
            canonicalQueryString,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        // String to sign
        var scope = $"{dateStamp}/{conn.Region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            HashSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        // Signing key
        var signingKey = DeriveSigningKey(conn.SecretAccessKey, dateStamp, conn.Region);
        var signature = HmacSha256Hex(signingKey, stringToSign);

        var authHeader = $"AWS4-HMAC-SHA256 Credential={conn.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, "s3");
        return HmacSha256(kService, "aws4_request");
    }

    private static string HashSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HmacSha256Hex(byte[] key, string data) =>
        Convert.ToHexStringLower(HmacSha256(key, data));
}
