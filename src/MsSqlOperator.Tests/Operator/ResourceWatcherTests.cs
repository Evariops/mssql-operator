using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Operator;

public class ResourceWatcherTests
{
    private static HttpOperationResponse Response(string body) =>
        new() { Response = new HttpResponseMessage { Content = new StringContent(body) } };

    [Fact]
    public async Task RunAsync_Relists_Then_DispatchesWatchEvents()
    {
        var relisted = new List<int>();
        var events = new List<(string Type, string Name)>();
        var call = 0;
        using var cts = new CancellationTokenSource();

        Task<HttpOperationResponse> OpenList(bool watch, string? rv, CancellationToken ct)
        {
            call++;
            if (!watch)
            {
                // Relist: two items at resourceVersion 100.
                return Task.FromResult(Response("""
                {"metadata":{"resourceVersion":"100"},"items":[
                    {"metadata":{"name":"a","namespace":"ns"}},
                    {"metadata":{"name":"b","namespace":"ns"}}]}
                """));
            }

            if (call == 2)
            {
                // One real event + a bookmark; the stream then ends (clean rotation).
                return Task.FromResult(Response(
                    "{\"type\":\"MODIFIED\",\"object\":{\"metadata\":{\"name\":\"a\",\"namespace\":\"ns\",\"resourceVersion\":\"101\"}}}\n" +
                    "{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"102\"}}}\n"));
            }

            // Third call (resume from 102): stop the loop.
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance, OpenList,
            items => relisted.Add(items.Count),
            (type, obj) => events.Add((type, obj.GetProperty("metadata").GetProperty("name").GetString()!)));

        await watcher.RunAsync(cts.Token);

        relisted.Should().ContainSingle().Which.Should().Be(2);
        // The bookmark advances the resume point but is not surfaced as an event.
        events.Should().ContainSingle().Which.Should().Be(("MODIFIED", "a"));
    }

    [Fact]
    public async Task RunAsync_ToleratesMalformedWatchLine_AndContinues()
    {
        var events = new List<string>();
        var call = 0;
        using var cts = new CancellationTokenSource();

        Task<HttpOperationResponse> OpenList(bool watch, string? rv, CancellationToken ct)
        {
            call++;
            if (!watch)
                return Task.FromResult(Response("""{"metadata":{"resourceVersion":"1"},"items":[]}"""));
            if (call == 2)
                return Task.FromResult(Response(
                    "this-is-not-json\n" +
                    "{\"type\":\"ADDED\",\"object\":{\"metadata\":{\"name\":\"ok\",\"resourceVersion\":\"2\"}}}\n"));
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance, OpenList,
            _ => { },
            (_, obj) => events.Add(obj.GetProperty("metadata").GetProperty("name").GetString()!));

        await watcher.RunAsync(cts.Token);

        // The poisoned line is skipped; the following valid event is still delivered.
        events.Should().ContainSingle().Which.Should().Be("ok");
    }

    [Fact]
    public async Task RunAsync_UpdatesLastActivity_OnRelist()
    {
        var time = new MsSqlOperator.Tests.Services.FakeTimeProvider();
        time.SetUtcNow(DateTimeOffset.UnixEpoch);
        using var cts = new CancellationTokenSource();

        Task<HttpOperationResponse> OpenList(bool watch, string? rv, CancellationToken ct)
        {
            if (!watch)
                return Task.FromResult(Response("""{"metadata":{"resourceVersion":"1"},"items":[]}"""));
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher("Test", NullLogger.Instance, OpenList, _ => { }, (_, _) => { }, time);

        await watcher.RunAsync(cts.Token);

        watcher.LastActivity.Should().Be(DateTimeOffset.UnixEpoch);
    }
}
