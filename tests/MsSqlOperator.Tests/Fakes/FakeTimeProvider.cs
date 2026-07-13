namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written controllable <see cref="TimeProvider"/> for tests (settable UTC clock).</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    /// <summary>Move the clock forward — used to cross wall-clock windows such as the
    /// unreachable-primary window without sleeping.</summary>
    public void Advance(TimeSpan delta) => _utcNow += delta;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
