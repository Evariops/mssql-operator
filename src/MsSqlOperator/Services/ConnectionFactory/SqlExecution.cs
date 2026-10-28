using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>
/// Runs a SQL operation so that a cancellation by the caller stays a cancellation — SqlClient
/// reports it as a SqlException ("Operation cancelled by user"), which a catch-all then counted as a
/// business failure — and so that only an operation declared idempotent is retried, with jitter, on
/// a transient error, a deadlock or a lock timeout.
/// </summary>
public static class SqlExecution
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct,
        bool idempotent = false, int retries = 2, ILogger? logger = null, string? what = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException($"{what ?? "SQL operation"} cancelled by the caller", ex, ct);
            }
            catch (SqlException ex) when (idempotent && attempt < retries && IsRetryable(SqlErrors.Classify(ex)))
            {
                var delay = Backoff(attempt);
                logger?.LogInformation(ex, "{What} failed ({Kind}); retrying in {Delay} ms", what ?? "SQL operation",
                    SqlErrors.Classify(ex), (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public static Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct,
        bool idempotent = false, int retries = 2, ILogger? logger = null, string? what = null)
        => RunAsync(async c => { await operation(c); return true; }, ct, idempotent, retries, logger, what);

    private static bool IsRetryable(SqlErrorKind kind)
        => SqlErrors.IsTransient(kind) || kind == SqlErrorKind.LockTimeout;

    /// <summary>200 ms, 400 ms, 800 ms… plus up to 50 % of jitter, so that the workers that hit the
    /// same lock do not come back together.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var baseMs = 200 * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(baseMs * (1 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(500) / 1000.0));
    }
}
