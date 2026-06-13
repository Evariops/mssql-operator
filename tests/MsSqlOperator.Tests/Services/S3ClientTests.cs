using System.Net;
using FluentAssertions;
using MsSqlOperator.Services.S3;

namespace MsSqlOperator.Tests.Services;

public class S3ClientTests
{
    private static readonly S3ConnectionInfo Conn =
        new("https://s3.example.com", "bucket", "us-east-1", "AKIA", "secret", ForcePathStyle: true);

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private static S3Client ClientReturning(HttpStatusCode status)
        => new(new HttpClient(new StubHandler(status)));

    [Fact]
    public async Task ObjectExistsAsync_Returns_True_On_200()
    {
        var result = await ClientReturning(HttpStatusCode.OK).ObjectExistsAsync(Conn, "key");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ObjectExistsAsync_Returns_False_On_404()
    {
        var result = await ClientReturning(HttpStatusCode.NotFound).ObjectExistsAsync(Conn, "key");
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ObjectExistsAsync_Throws_On_Unexpected_Status(HttpStatusCode status)
    {
        var act = async () => await ClientReturning(status).ObjectExistsAsync(Conn, "key");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── ────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string Body { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body),
            });
        }
    }

    [Fact]
    public async Task PlaintextEndpoint_RejectedWithoutOptIn()
    {
        //
        var conn = Conn with { Endpoint = "http://minio.local" };
        var act = async () => await new S3Client(new HttpClient(new CapturingHandler())).GetObjectAsync(conn, "k");
        await act.Should().ThrowAsync<InvalidOperationException>().Where(e => e.Message.Contains("not HTTPS"));
    }

    [Fact]
    public async Task PlaintextEndpoint_AllowedWithOptIn()
    {
        var conn = Conn with { Endpoint = "http://minio.local", AllowInsecureEndpoint = true };
        await new S3Client(new HttpClient(new CapturingHandler())).GetObjectAsync(conn, "k");
    }

    [Fact]
    public async Task PathTraversalKey_Rejected()
    {
        //
        var act = async () => await new S3Client(new HttpClient(new CapturingHandler())).GetObjectAsync(Conn, "a/../b");
        await act.Should().ThrowAsync<InvalidOperationException>().Where(e => e.Message.Contains("traversal"));
    }

    [Fact]
    public async Task SigV4_ProducesAuthorizationAndAmzHeaders()
    {
        // The manual SigV4 signing is the TDE backup channel — assert the
        // structural invariants of the signature on every request.
        var handler = new CapturingHandler();
        await new S3Client(new HttpClient(handler)).GetObjectAsync(Conn, "tde/cert/v1.enc");

        var req = handler.Last!;
        var auth = req.Headers.GetValues("Authorization").Single();
        auth.Should().StartWith("AWS4-HMAC-SHA256 Credential=AKIA/");
        auth.Should().Contain("/us-east-1/s3/aws4_request");
        auth.Should().Contain("SignedHeaders=host;x-amz-content-sha256;x-amz-date");
        auth.Should().MatchRegex("Signature=[0-9a-f]{64}$");
        req.Headers.GetValues("x-amz-date").Single().Should().MatchRegex(@"^\d{8}T\d{6}Z$");
        // Empty-payload GET uses the well-known SHA-256 of the empty string.
        req.Headers.GetValues("x-amz-content-sha256").Single()
            .Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task ListObjects_FollowsContinuationToken()
    {
        // Paginate past the first page.
        var page1 = "<ListBucketResult><Contents><Key>a</Key></Contents>" +
                    "<IsTruncated>true</IsTruncated><NextContinuationToken>TOK</NextContinuationToken></ListBucketResult>";
        var page2 = "<ListBucketResult><Contents><Key>b</Key></Contents><IsTruncated>false</IsTruncated></ListBucketResult>";
        var calls = 0;
        var handler = new SeqHandler(_ => calls++ == 0 ? page1 : page2);
        var keys = await new S3Client(new HttpClient(handler)).ListObjectsAsync(Conn, "prefix");
        keys.Should().BeEquivalentTo("a", "b");
        calls.Should().Be(2);
    }

    private sealed class SeqHandler(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request)) });
    }
}
