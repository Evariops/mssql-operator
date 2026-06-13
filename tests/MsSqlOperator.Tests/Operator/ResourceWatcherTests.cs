using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Operator;

public class ResourceWatcherTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task RunAsync_Relists_Then_RunsWatchSession()
    {
        var relisted = new List<int>();
        var events = new List<(string Type, string Name)>();
        var listCalls = 0;
        var watchCalls = 0;
        using var cts = new CancellationTokenSource();

        // Relist returns a JSON body (the already-deserialized list, re-serialized).
        Task<string> ListJson(CancellationToken ct)
        {
            listCalls++;
            return Task.FromResult("""
            {"metadata":{"resourceVersion":"100"},"items":[
                {"metadata":{"name":"a","namespace":"ns"}},
                {"metadata":{"name":"b","namespace":"ns"}}]}
            """);
        }

        // The watch session emits one event, then a second invocation ends the loop.
        Task<string?> WatchSession(string? rv, Action onActivity, Action<string, JsonElement> emit, CancellationToken ct)
        {
            watchCalls++;
            if (watchCalls == 1)
            {
                onActivity();
                emit("MODIFIED", Json("""{"metadata":{"name":"a","namespace":"ns","resourceVersion":"101"}}"""));
                return Task.FromResult<string?>("102");
            }
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance, ListJson, WatchSession,
            items => relisted.Add(items.Count),
            (type, obj) => events.Add((type, obj.GetProperty("metadata").GetProperty("name").GetString()!)));

        await watcher.RunAsync(cts.Token);

        listCalls.Should().BeGreaterThan(0);
        relisted.Should().ContainSingle().Which.Should().Be(2);
        events.Should().ContainSingle().Which.Should().Be(("MODIFIED", "a"));
    }

    [Fact]
    public async Task RunAsync_StopsQuietly_WhenResourceTypeNotServed()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5)); // safety net so the test never hangs

        // The relist throws 404 (optional CRD not installed). With stopIfResourceAbsent the watcher
        // disables itself instead of retrying every 5s forever.
        Task<string> ListJson(CancellationToken ct) =>
            throw new HttpOperationException
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), "404 page not found"),
            };

        var watcher = new ResourceWatcher(
            "Optional", NullLogger.Instance, ListJson,
            (_, _, _, _) => Task.FromResult<string?>(null), _ => { }, (_, _) => { },
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

        Task<string?> WatchSession(string? rv, Action onActivity, Action<string, JsonElement> emit, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }

        var watcher = new ResourceWatcher(
            "Test", NullLogger.Instance,
            _ => Task.FromResult("""{"metadata":{"resourceVersion":"1"},"items":[]}"""),
            WatchSession, _ => { }, (_, _) => { }, time);

        await watcher.RunAsync(cts.Token);

        watcher.LastActivity.Should().Be(DateTimeOffset.UnixEpoch);
    }
}
