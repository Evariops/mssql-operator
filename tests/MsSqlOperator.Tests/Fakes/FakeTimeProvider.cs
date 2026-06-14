namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written controllable <see cref="TimeProvider"/> for tests (settable UTC clock).</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
