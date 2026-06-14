using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IEntityRuntime"/>: records enqueued parent keys.</summary>
public sealed class FakeEntityRuntime(string entityKind) : IEntityRuntime
{
    public string EntityKind { get; } = entityKind;
    public readonly List<string> EnqueueCalls = [];

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    public void Enqueue(string key) => EnqueueCalls.Add(key);
}
