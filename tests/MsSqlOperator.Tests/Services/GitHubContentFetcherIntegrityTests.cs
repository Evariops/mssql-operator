using System.Net;
using System.Security.Cryptography;
using System.Text;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.GitHub;

namespace MsSqlOperator.Tests.Services;

/// <summary> acceptance: pinning, blob integrity, typed errors (stubbed HTTP).</summary>
public class GitHubContentFetcherIntegrityTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static string GitBlobSha(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. content])).ToLowerInvariant();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static GitHubContentFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new StubHandler(respond)));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string TreeJson(params (string path, string sha)[] blobs) =>
        $$"""
        {"truncated": false, "tree": [{{string.Join(",", blobs.Select(b =>
            $$"""{"path": "{{b.path}}", "type": "blob", "sha": "{{b.sha}}"}"""))}}]}
        """;

    private static string BlobJson(byte[] content) =>
        $$"""{"content": "{{Convert.ToBase64String(content)}}", "encoding": "base64"}""";

    private static string TagRefJson(string type, string sha) =>
        $$"""{"object": {"type": "{{type}}", "sha": "{{sha}}"} }""";

    [Fact]
    public async Task FetchFiles_WithoutFullSha_Throws()
    {
        var fetcher = Fetcher(_ => throw new InvalidOperationException("no request expected"));
        var act = () => fetcher.FetchFilesAsync(new GitHubSourceSpec { Repository = "o/r" }, "HEAD", null);
        var ex = await Assert.ThrowsAsync<GitHubFetchException>(act);
        Assert.Contains("40-hex", ex.Message);
    }

    [Fact]
    public async Task FetchFiles_ValidBlob_ReturnsVerifiedContent()
    {
        var content = Encoding.UTF8.GetBytes("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1");
        var blobSha = GitBlobSha(content);
        var fetcher = Fetcher(req => req.RequestUri!.AbsolutePath.Contains("/git/trees/")
            ? Json(TreeJson(("procs/p.sql", blobSha)))
            : Json(BlobJson(content)));

        var files = await fetcher.FetchFilesAsync(
            new GitHubSourceSpec { Repository = "o/r", Include = ["procs/**"] }, CommitSha, null);

        Assert.Contains("PROCEDURE dbo.p", Assert.Single(files).content);
    }

    [Fact]
    public async Task FetchFiles_TamperedBlob_ThrowsIntegrityException()
    {
        var announced = Encoding.UTF8.GetBytes("SELECT 1");
        var tampered = Encoding.UTF8.GetBytes("EXEC xp_cmdshell 'evil'");
        var fetcher = Fetcher(req => req.RequestUri!.AbsolutePath.Contains("/git/trees/")
            ? Json(TreeJson(("p.sql", GitBlobSha(announced))))
            : Json(BlobJson(tampered)));

        var act = () => fetcher.FetchFilesAsync(
            new GitHubSourceSpec { Repository = "o/r", Include = ["**"] }, CommitSha, null);

        await Assert.ThrowsAsync<GitHubIntegrityException>(act);
    }

    [Fact]
    public async Task FetchFiles_TruncatedTree_Throws()
    {
        var fetcher = Fetcher(_ => Json("""{"truncated": true, "tree": []}"""));
        var act = () => fetcher.FetchFilesAsync(
            new GitHubSourceSpec { Repository = "o/r", Include = ["**"] }, CommitSha, null);
        var ex = await Assert.ThrowsAsync<GitHubFetchException>(act);
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public async Task SharedCache_TreeFetchedWithAToken_IsNotServedToACallerWithoutIt()
    {
        // The cache is a process-wide singleton: keyed by repository@sha only, a CR with no token
        // got the private tree (and the blobs behind it) another namespace's token had fetched.
        var content = Encoding.UTF8.GetBytes("CREATE OR ALTER PROCEDURE dbo.secret AS SELECT 1");
        var cache = new GitHubContentCache();
        var source = new GitHubSourceSpec { Repository = "org/private", Include = ["**"] };
        HttpResponseMessage Private(HttpRequestMessage req) => req.Headers.Authorization is null
            ? Json("""{"message": "Not Found"}""", HttpStatusCode.NotFound)
            : req.RequestUri!.AbsolutePath.Contains("/git/trees/")
                ? Json(TreeJson(("p.sql", GitBlobSha(content))))
                : Json(BlobJson(content));

        var authorized = new GitHubContentFetcher(new HttpClient(new StubHandler(Private)), cache);
        Assert.Single(await authorized.FetchFilesAsync(source, CommitSha, "tenant-a-token"));

        var anonymous = new GitHubContentFetcher(new HttpClient(new StubHandler(Private)), cache);
        var ex = await Assert.ThrowsAsync<GitHubFetchException>(() => anonymous.FetchFilesAsync(source, CommitSha, null));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task SharedCache_SameCredential_IsServedWithoutARequest()
    {
        var content = Encoding.UTF8.GetBytes("SELECT 1");
        var cache = new GitHubContentCache();
        var requests = 0;
        var source = new GitHubSourceSpec { Repository = "o/r", Include = ["**"] };
        HttpResponseMessage Counting(HttpRequestMessage req)
        {
            requests++;
            return req.RequestUri!.AbsolutePath.Contains("/git/trees/")
                ? Json(TreeJson(("p.sql", GitBlobSha(content))))
                : Json(BlobJson(content));
        }

        await new GitHubContentFetcher(new HttpClient(new StubHandler(Counting)), cache).FetchFilesAsync(source, CommitSha, "t");
        await new GitHubContentFetcher(new HttpClient(new StubHandler(Counting)), cache).FetchFilesAsync(source, CommitSha, "t");

        Assert.Equal(2, requests); // one tree + one blob, the second fetch hits the cache
    }

    [Fact]
    public async Task FetchFiles_ExplicitPathMissingFromTree_Throws()
    {
        var fetcher = Fetcher(_ => Json(TreeJson(("other.sql", GitBlobSha([1, 2, 3])))));
        var act = () => fetcher.FetchFilesAsync(
            new GitHubSourceSpec { Repository = "o/r", Paths = [new GitHubPathSpec { Path = "missing.sql" }] },
            CommitSha, null);
        var ex = await Assert.ThrowsAsync<GitHubFetchException>(act);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task RateLimited403_ThrowsTypedException()
    {
        var fetcher = Fetcher(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            resp.Headers.Add("X-RateLimit-Remaining", "0");
            resp.Headers.Add("X-RateLimit-Reset", "4102444800");
            return resp;
        });
        var act = () => fetcher.ResolveTagToCommitShaAsync("o/r", "v1.0.0", null);
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(act);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), ex.ResetAt);
    }

    [Fact]
    public async Task ResolveTag_LightweightTag_ReturnsCommitSha()
    {
        var fetcher = Fetcher(_ => Json(TagRefJson("commit", CommitSha)));
        Assert.Equal(CommitSha, (await fetcher.ResolveTagToCommitShaAsync("o/r", "v1.0.0", null)));
    }

    [Fact]
    public async Task ResolveTag_AnnotatedTag_DereferencesToCommit()
    {
        const string tagObjSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var fetcher = Fetcher(req => req.RequestUri!.AbsolutePath.Contains("/git/ref/tags/")
            ? Json(TagRefJson("tag", tagObjSha))
            : Json(TagRefJson("commit", CommitSha)));
        Assert.Equal(CommitSha, (await fetcher.ResolveTagToCommitShaAsync("o/r", "v1.0.0", null)));
    }

    [Fact]
    public async Task ServerError_ThrowsTypedException()
    {
        var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") });
        var act = () => fetcher.ResolveTagToCommitShaAsync("o/r", "v1.0.0", null);
        var ex = await Assert.ThrowsAsync<GitHubFetchException>(act);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }
}
