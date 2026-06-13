using System.Net;
using System.Net.Http;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Operator;

public class ResourceWatcherTests
{
    private static HttpOperationResponse WatchResponse(string body) =>
        new() { Response = new HttpResponseMessage { Content = new StringContent(body) } };

    [Fact]
    public async Task RunAsync_Relists_Then_DispatchesWatchEvents()
    {
        var relisted = new List<int>();
        var events = new List<(string Type, string Name)>();
        var listCalls = 0;
        var watchCalls = 0;
        using var cts = new CancellationTokenSource();

        // Relist returns a JSON body (already-deserialized list — never a re-read of the stream).
        Task<string> ListJson(CancellationToken ct)
        {
            listCalls++;
            return Task.FromResult("""
            {"metadata":{"resourceVersion":"100"},"items":[
                {"metadata":{"name":"a","namespace":"ns"}},
                {"metadata":{"name":"b","namespace":"ns"}}]}
            """);
        }

        Task<HttpOperationResponse> OpenWatch(string? rv, CancellationToken ct)
        {
            watchCalls++;
            if (watchCalls == 1)
                return Task.FromResult(WatchResponse(
                    "{\"type\":\"MODIFIED\",\"object\":{\"metadata\":{\"name\":\"a\",\"namespace\":\"ns\",\"resourceVersion\":\"101\"}}}\n" +
                    "{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"102\"}}}\n"));
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance, ListJson, OpenWatch,
            items => relisted.Add(items.Count),
            (type, obj) => events.Add((type, obj.GetProperty("metadata").GetProperty("name").GetString()!)));

        await watcher.RunAsync(cts.Token);

        listCalls.Should().BeGreaterThan(0);
        relisted.Should().ContainSingle().Which.Should().Be(2);
        // The bookmark advances the resume point but is not surfaced as an event.
        events.Should().ContainSingle().Which.Should().Be(("MODIFIED", "a"));
    }

    [Fact]
    public async Task RunAsync_ToleratesMalformedWatchLine_AndContinues()
    {
        var events = new List<string>();
        var watchCalls = 0;
        using var cts = new CancellationTokenSource();

        Task<HttpOperationResponse> OpenWatch(string? rv, CancellationToken ct)
        {
            watchCalls++;
            if (watchCalls == 1)
                return Task.FromResult(WatchResponse(
                    "this-is-not-json\n" +
                    "{\"type\":\"ADDED\",\"object\":{\"metadata\":{\"name\":\"ok\",\"resourceVersion\":\"2\"}}}\n"));
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance,
            _ => Task.FromResult("""{"metadata":{"resourceVersion":"1"},"items":[]}"""),
            OpenWatch, _ => { },
            (_, obj) => events.Add(obj.GetProperty("metadata").GetProperty("name").GetString()!));

        await watcher.RunAsync(cts.Token);

        // The poisoned line is skipped; the following valid event is still delivered.
        events.Should().ContainSingle().Which.Should().Be("ok");
    }

    [Fact]
    public async Task RunAsync_StopsQuietly_WhenResourceTypeNotServed()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5)); // safety net so the test never hangs

        // The list throws 404 (optional CRD not installed). With stopIfResourceAbsent the watcher
        // disables itself instead of retrying every 5s forever.
        Task<string> ListJson(CancellationToken ct) =>
            throw new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), "404 page not found"),
            };

        var watcher = new ResourceWatcher(
            "Optional", NullLogger.Instance, ListJson,
            (_, _) => Task.FromResult(WatchResponse("")), _ => { }, (_, _) => { },
            stopIfResourceAbsent: true);

        await watcher.RunAsync(cts.Token);

        watcher.Stopped.Should().BeTrue();
        cts.IsCancellationRequested.Should().BeFalse("the watcher returns on 404, it does not run until the safety timeout");
    }

    [Fact]
    public async Task RunAsync_UpdatesLastActivity_OnRelist()
    {
        var time = new MsSqlOperator.Tests.Services.FakeTimeProvider();
        time.SetUtcNow(DateTimeOffset.UnixEpoch);
        using var cts = new CancellationTokenSource();

        Task<HttpOperationResponse> OpenWatch(string? rv, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance,
            _ => Task.FromResult("""{"metadata":{"resourceVersion":"1"},"items":[]}"""),
            OpenWatch, _ => { }, (_, _) => { }, time);

        await watcher.RunAsync(cts.Token);

        watcher.LastActivity.Should().Be(DateTimeOffset.UnixEpoch);
    }
}
