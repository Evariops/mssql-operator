using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.GitHub;

public interface IGitHubContentFetcher
{
    /// <summary>
    /// Resolves a tag to the full commit SHA it points at, following annotated tag objects.
    /// Used to pin mutable refs (tags, followLatestRelease) at reconcile time.
    /// </summary>
    Task<string> ResolveTagToCommitShaAsync(string repository, string tag, string? token, CancellationToken ct = default);

    Task<string?> GetLatestReleaseTagAsync(string repository, string? releaseFilter, string? token, CancellationToken ct = default);

    /// <summary>
    /// Fetches the selected files pinned at <paramref name="commitSha"/> (full 40-hex SHA required).
    /// Every blob is downloaded through the Blobs API (no 1 MB Contents limit) and verified against
    /// the git SHA announced by the Trees API; any mismatch throws <see cref="GitHubIntegrityException"/>.
    /// </summary>
    Task<List<(string path, string content)>> FetchFilesAsync(GitHubSourceSpec source, string commitSha, string? token, CancellationToken ct = default);
}

/// <summary>
/// A tree at a commit SHA and a blob at a git SHA are immutable, so both are cached across
/// passes (singleton; the typed HTTP client itself is transient). Every resync used to re-download
/// the whole tree and every blob — the unauthenticated limit is 60 requests an hour. Bounded: cleared
/// when full, which only costs a re-download.
/// </summary>
public sealed class GitHubContentCache
{
    internal const int MaxCachedTrees = 64;
    internal const int MaxCachedBlobs = 4096;
    internal readonly ConcurrentDictionary<string, Dictionary<string, string>> Trees = new(StringComparer.Ordinal);
    internal readonly ConcurrentDictionary<string, string> Blobs = new(StringComparer.Ordinal);
}

public partial class GitHubContentFetcher(HttpClient httpClient, GitHubContentCache? cache = null) : IGitHubContentFetcher
{
    private const string GitHubApiBase = "https://api.github.com";

    private readonly GitHubContentCache _cache = cache ?? new GitHubContentCache();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase)]
    private static partial Regex FullShaRegex();

    public static bool IsFullCommitSha(string? value) => value != null && FullShaRegex().IsMatch(value);

    public async Task<List<(string path, string content)>> FetchFilesAsync(GitHubSourceSpec source, string commitSha, string? token, CancellationToken ct = default)
    {
        if (!IsFullCommitSha(commitSha))
            throw new GitHubFetchException($"GitHub source requires a full 40-hex commit SHA; got '{commitSha}'. Pin ref.sha or use a tag/followLatestRelease so the operator can resolve one.");

        var tree = await ListTreeAsync(source.Repository, commitSha, token, ct);
        var files = new List<(string path, string content)>();

        if (source.Paths != null)
        {
            foreach (var pathSpec in source.Paths)
            {
                if (!tree.TryGetValue(pathSpec.Path, out var blobSha))
                    throw new GitHubFetchException($"File '{pathSpec.Path}' not found in {source.Repository}@{commitSha}.", HttpStatusCode.NotFound);
                files.Add((pathSpec.Path, await FetchBlobAsync(source.Repository, pathSpec.Path, blobSha, token, ct)));
            }
        }

        if (source.Include != null)
        {
            foreach (var (path, blobSha) in tree)
            {
                if (!path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchesGlobPatterns(path, source.Include, source.Exclude)) continue;
                files.Add((path, await FetchBlobAsync(source.Repository, path, blobSha, token, ct)));
            }
        }

        return files;
    }

    public async Task<string> ResolveTagToCommitShaAsync(string repository, string tag, string? token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{GitHubApiBase}/repos/{repository}/git/ref/tags/{Uri.EscapeDataString(tag)}", token, ct);
        var obj = doc.RootElement.GetProperty("object");
        var sha = obj.GetProperty("sha").GetString()
                  ?? throw new GitHubFetchException($"Tag '{tag}' in {repository} has no object SHA.");

        // Annotated tags point to a tag object; dereference it to the commit.
        if (obj.GetProperty("type").GetString() == "tag")
        {
            using var tagDoc = await GetJsonAsync($"{GitHubApiBase}/repos/{repository}/git/tags/{sha}", token, ct);
            sha = tagDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()
                  ?? throw new GitHubFetchException($"Annotated tag '{tag}' in {repository} does not dereference to a commit.");
        }

        if (!IsFullCommitSha(sha))
            throw new GitHubFetchException($"Tag '{tag}' in {repository} resolved to non-SHA '{sha}'.");
        return sha;
    }

    public async Task<string?> GetLatestReleaseTagAsync(string repository, string? releaseFilter, string? token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{GitHubApiBase}/repos/{repository}/releases", token, ct);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString();
            if (tagName == null) continue;
            if (release.GetProperty("draft").GetBoolean()) continue;

            if (releaseFilter == null || MatchesFilter(tagName, releaseFilter))
                return tagName;
        }

        return null;
    }

    /// <summary>Lists the full tree at the commit and returns path → blob SHA.</summary>
    private async Task<Dictionary<string, string>> ListTreeAsync(string repository, string commitSha, string? token, CancellationToken ct)
    {
        var cacheKey = $"{repository.ToLowerInvariant()}@{commitSha.ToLowerInvariant()}";
        if (_cache.Trees.TryGetValue(cacheKey, out var cached))
            return cached;

        using var doc = await GetJsonAsync($"{GitHubApiBase}/repos/{repository}/git/trees/{commitSha}?recursive=1", token, ct);

        if (doc.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            throw new GitHubFetchException($"Tree of {repository}@{commitSha} is truncated by the GitHub API; the repository is too large to enumerate safely. Use explicit paths.");

        var tree = doc.RootElement.GetProperty("tree")
            .EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .ToDictionary(
                e => e.GetProperty("path").GetString()!,
                e => e.GetProperty("sha").GetString()!,
                StringComparer.Ordinal);

        if (_cache.Trees.Count >= GitHubContentCache.MaxCachedTrees)
            _cache.Trees.Clear();
        _cache.Trees[cacheKey] = tree;
        return tree;
    }

    /// <summary>
    /// Downloads a blob and verifies it against the git SHA from the tree:
    /// sha1("blob {length}\0" + content) must equal the announced object id. This binds the
    /// downloaded bytes to the pinned commit (git's own integrity scheme — SHA-1 here is the
    /// git object id format, not an operator crypto choice).
    /// </summary>
    private async Task<string> FetchBlobAsync(string repository, string path, string blobSha, string? token, CancellationToken ct)
    {
        // Content-addressed: the git SHA identifies the bytes whatever the repository or path.
        if (_cache.Blobs.TryGetValue(blobSha.ToLowerInvariant(), out var cached))
            return cached;

        var content = await DownloadBlobAsync(repository, path, blobSha, token, ct);
        if (_cache.Blobs.Count >= GitHubContentCache.MaxCachedBlobs)
            _cache.Blobs.Clear();
        _cache.Blobs[blobSha.ToLowerInvariant()] = content;
        return content;
    }

    private async Task<string> DownloadBlobAsync(string repository, string path, string blobSha, string? token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{GitHubApiBase}/repos/{repository}/git/blobs/{blobSha}", token, ct);

        var base64 = doc.RootElement.GetProperty("content").GetString()?.Replace("\n", "")
                     ?? throw new GitHubFetchException($"Blob {blobSha} ({path}) in {repository} has no content.");
        var bytes = Convert.FromBase64String(base64);

        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        var hashed = SHA1.HashData([.. header, .. bytes]);
        var actualSha = Convert.ToHexString(hashed).ToLowerInvariant();
        if (!string.Equals(actualSha, blobSha, StringComparison.OrdinalIgnoreCase))
            throw new GitHubIntegrityException($"Blob '{path}' in {repository}: content hash {actualSha} does not match announced git SHA {blobSha}. Refusing to deploy tampered or corrupted content.");

        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? token, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, url, token);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubFetchException($"GitHub request failed: GET {url}: {e.Message}", null, e);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response, out var resetAt))
                throw new GitHubRateLimitException($"GitHub rate limit exhausted on GET {url} (resets at {resetAt:O}).", resetAt);

            if (!response.IsSuccessStatusCode)
                throw new GitHubFetchException($"GitHub returned {(int)response.StatusCode} {response.StatusCode} on GET {url}.", response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response, out DateTimeOffset? resetAt)
    {
        resetAt = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            resetAt = DateTimeOffset.UtcNow + delta;
            return true;
        }
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), out var epoch))
                resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }
        return false;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MsSqlOperator", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    private static bool MatchesGlobPatterns(string path, List<string>? includes, List<string>? excludes)
    {
        if (includes != null && !includes.Any(p => GlobMatch(path, p))) return false;
        if (excludes != null && excludes.Any(p => GlobMatch(path, p))) return false;
        return true;
    }

    // Globs and release filters come from CR authors. Translated to regexes, a pattern such as
    // "**a**a**a**b" backtracks polynomially on a long path; the non-backtracking engine is linear.
    private static bool MatchesFilter(string tagName, string filter)
    {
        var pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(tagName, pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", "§§")
            .Replace("\\*", "[^/]*")
            .Replace("§§", ".*") + "$";
        return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    }
}
