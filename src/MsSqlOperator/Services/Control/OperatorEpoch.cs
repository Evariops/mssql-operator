using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// The operator's epoch. Each leadership takes a new one, strictly greater than every
/// epoch seen — in the counter of the <c>mssql-operator-epoch</c> Lease and in every control record —
/// by compare-and-swap on the Lease. A leader that was paused and comes back carries an older epoch:
/// its writes to the control records are refused by the API server (CEL), its SQL commands by the
/// epoch stamps.
/// </summary>
public interface IOperatorEpoch
{
    /// <summary>The epoch of this leadership; 0 before one was taken.</summary>
    long Current { get; }

    /// <summary>Takes a new epoch, at the start of a leadership.</summary>
    Task<long> AdvanceAsync(CancellationToken ct);

    /// <summary>An epoch at least as high was seen (a SQL stamp): take one above it.</summary>
    Task<long> RaiseAboveAsync(long seen, CancellationToken ct);
}

public sealed class OperatorEpoch(IKubernetesClient client, string @namespace, ILogger<OperatorEpoch>? logger = null) : IOperatorEpoch
{
    public const string LeaseName = "mssql-operator-epoch";

    private long _current;

    public long Current => Interlocked.Read(ref _current);

    public Task<long> AdvanceAsync(CancellationToken ct) => RaiseAboveAsync(0, ct);

    /// <summary>Attempts before the failure reaches the caller (at the start of a leadership, the process stops).</summary>
    private const int Attempts = 8;

    public async Task<long> RaiseAboveAsync(long seen, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var records = await client.ListAsync<SqlControlRecord>(null, null, ct);
                var highest = Math.Max(seen, records.Select(r => r.Spec?.Epoch ?? 0).DefaultIfEmpty(0).Max());
                var lease = await client.GetAsync<V1Lease>(LeaseName, @namespace, ct);
                var counter = lease?.Spec?.LeaseTransitions ?? 0;
                var next = Math.Max(counter, highest) + 1;
                if (next > int.MaxValue)
                    throw new OverflowException("the operator epoch overflowed the Lease counter");
                if (lease is null)
                    await client.CreateAsync(new V1Lease
                    {
                        Metadata = new V1ObjectMeta { Name = LeaseName, NamespaceProperty = @namespace },
                        Spec = new V1LeaseSpec { LeaseTransitions = (int)next },
                    }, ct);
                else
                    await client.PatchAsync(lease, [JsonPatch.Replace("/spec/leaseTransitions", (int)next)], ct);
                Interlocked.Exchange(ref _current, next);
                logger?.LogInformation("Operator epoch is now {Epoch}", next);
                return next;
            }
            catch (Exception ex) when (attempt < Attempts - 1 && !ct.IsCancellationRequested && ex is not OverflowException)
            {
                // Another writer moved the counter between the read and the write, or the API server
                // did not answer: read again after a pause. Five immediate attempts used to lose
                // against a few seconds of API unavailability — and a lost epoch stops the process.
                logger?.LogInformation(ex, "Epoch compare-and-swap lost or API unavailable (attempt {Attempt}); retrying", attempt + 1);
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }
    }

    /// <summary>200 ms doubling up to 5 s, with jitter, so two candidates do not retry in step.</summary>
    internal static TimeSpan RetryDelay(int attempt)
    {
        var baseMs = Math.Min(5000, 200 * (1 << Math.Min(attempt, 5)));
        return TimeSpan.FromMilliseconds(baseMs / 2 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(baseMs / 2 + 1));
    }
}
