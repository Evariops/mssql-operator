using System.Net.Http.Headers;
using System.Text.Json;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.GitHub;

public interface IGitHubContentFetcher
{
    Task<List<(string path, string content)>> FetchFilesAsync(GitHubSourceSpec source, string? token, CancellationToken ct = default);
    Task<string?> ResolveTagToShaAsync(string repository, string tag, string? token, CancellationToken ct = default);
    Task<string?> GetLatestReleaseTagAsync(string repository, string? releaseFilter, string? token, CancellationToken ct = default);
}

public class GitHubContentFetcher(HttpClient httpClient) : IGitHubContentFetcher
{
    private const string GitHubApiBase = "https://api.github.com";

    public async Task<List<(string path, string content)>> FetchFilesAsync(GitHubSourceSpec source, string? token, CancellationToken ct = default)
    {
        var sha = source.Ref?.Sha;
        if (sha == null && source.Ref?.Tag != null)
        {
            sha = await ResolveTagToShaAsync(source.Repository, source.Ref.Tag, token, ct);
        }

        var files = new List<(string path, string content)>();

        if (source.Paths != null)
        {
            foreach (var pathSpec in source.Paths)
            {
                var content = await FetchFileContentAsync(source.Repository, pathSpec.Path, sha, token, ct);
                if (content != null)
                {
                    files.Add((pathSpec.Path, content));
                }
            }
        }

        if (source.Include != null)
        {
            var treeFiles = await ListTreeFilesAsync(source.Repository, sha, token, ct);
            foreach (var filePath in treeFiles)
            {
                if (!filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchesGlobPatterns(filePath, source.Include, source.Exclude)) continue;

                var content = await FetchFileContentAsync(source.Repository, filePath, sha, token, ct);
                if (content != null)
                {
                    files.Add((filePath, content));
                }
            }
        }

        return files;
    }

    public async Task<string?> ResolveTagToShaAsync(string repository, string tag, string? token, CancellationToken ct = default)
    {
        var url = $"{GitHubApiBase}/repos/{repository}/git/refs/tags/{tag}";
        var request = CreateRequest(HttpMethod.Get, url, token);
        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
    }

    public async Task<string?> GetLatestReleaseTagAsync(string repository, string? releaseFilter, string? token, CancellationToken ct = default)
    {
        var url = $"{GitHubApiBase}/repos/{repository}/releases";
        var request = CreateRequest(HttpMethod.Get, url, token);
        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString();
            if (tagName == null) continue;
            if (release.GetProperty("draft").GetBoolean()) continue;

            if (releaseFilter == null || MatchesFilter(tagName, releaseFilter))
            {
                return tagName;
            }
        }

        return null;
    }

    private async Task<string?> FetchFileContentAsync(string repository, string path, string? sha, string? token, CancellationToken ct)
    {
        var refParam = sha != null ? $"?ref={sha}" : "";
        var url = $"{GitHubApiBase}/repos/{repository}/contents/{path}{refParam}";
        var request = CreateRequest(HttpMethod.Get, url, token);
        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("content", out var contentProp))
        {
            var base64 = contentProp.GetString()?.Replace("\n", "");
            if (base64 != null)
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
        }

        return null;
    }

    private async Task<List<string>> ListTreeFilesAsync(string repository, string? sha, string? token, CancellationToken ct)
    {
        var refParam = sha ?? "HEAD";
        var url = $"{GitHubApiBase}/repos/{repository}/git/trees/{refParam}?recursive=1";
        var request = CreateRequest(HttpMethod.Get, url, token);
        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("tree")
            .EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString()!)
            .ToList();
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

    private static bool MatchesFilter(string tagName, string filter)
    {
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(filter).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(tagName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", "§§")
            .Replace("\\*", "[^/]*")
            .Replace("§§", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
