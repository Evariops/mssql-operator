using System.Net;
using MsSqlOperator.Services.GitHub;

namespace MsSqlOperator.Tests.Services;

/// <summary>The periodic sync asks GitHub "changed since this ETag?".</summary>
public class GitHubConditionalRequestTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<string?> IfNoneMatch { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var etag = request.Headers.TryGetValues("If-None-Match", out var values) ? values.Single() : null;
            IfNoneMatch.Add(etag);
            if (etag == "\"v1\"")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"tag_name":"v1.2.0","draft":false}]"""),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SecondPoll_IsConditional_AndAnsweredFromTheCacheOn304()
    {
        var handler = new Handler();
        var fetcher = new GitHubContentFetcher(new HttpClient(handler));

        Assert.Equal("v1.2.0", await fetcher.GetLatestReleaseTagAsync("org/repo", null, "token"));
        Assert.Equal("v1.2.0", await fetcher.GetLatestReleaseTagAsync("org/repo", null, "token"));

        Assert.Equal([null, "\"v1\""], handler.IfNoneMatch);
    }
}
