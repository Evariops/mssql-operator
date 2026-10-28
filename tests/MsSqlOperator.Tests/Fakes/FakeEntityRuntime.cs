using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IEntityRuntime"/>: records enqueued parent keys.</summary>
public sealed class FakeEntityRuntime(string entityKind) : IEntityRuntime
{
    public string EntityKind { get; } = entityKind;
    public readonly List<string> EnqueueCalls = [];

    /// <summary>The keys of the resources the runtime knows, by UID.</summary>
    public readonly Dictionary<string, string> KeysByUid = [];

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    public void Enqueue(string key) => EnqueueCalls.Add(key);
    public string? KeyOfUid(string uid) => KeysByUid.GetValueOrDefault(uid);
}
