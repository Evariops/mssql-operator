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
}
