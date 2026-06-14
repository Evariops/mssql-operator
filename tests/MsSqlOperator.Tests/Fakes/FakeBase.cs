namespace MsSqlOperator.Tests.Fakes;

/// <summary>
/// Base for hand-written fakes (replaces Moq): records every call as (method, args) and exposes a
/// per-method configurable result. Assert interactions with <see cref="Count"/> / <see cref="ArgsFor"/>;
/// configure return values with <see cref="SetResult"/> and read them with <see cref="Result{T}"/>.
/// </summary>
public abstract class FakeBase
{
    public readonly List<(string Method, object?[] Args)> Calls = [];
    private readonly Dictionary<string, object?> _results = new();

    protected void Rec(string method, params object?[] args) => Calls.Add((method, args));

    protected T Result<T>(string method, T fallback) => _results.TryGetValue(method, out var v) ? (T)v! : fallback;

    /// <summary>Configure the value a query method returns (keyed by method name).</summary>
    public void SetResult(string method, object? value) => _results[method] = value;

    public int Count(string method) => Calls.Count(c => c.Method == method);

    public IEnumerable<object?[]> ArgsFor(string method) => Calls.Where(c => c.Method == method).Select(c => c.Args);
}
