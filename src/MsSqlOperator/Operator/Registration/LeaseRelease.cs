using k8s.LeaderElection;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.Registration;

/// <summary>
/// A replica that stops gracefully hands its Lease over instead of letting it expire.
/// Without it, every drain or rollout of the node running the leader left the fleet without a
/// reconciler for the whole lease duration (30 s), failover detection included. The standby replica
/// takes the released Lease on its next retry (2 s).
///
/// The caller releases only once everything that writes under the Lease has stopped: from then on,
/// another replica may be reconciling.
/// </summary>
public static class LeaseRelease
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Releases <paramref name="leaseLock"/> when this replica still holds it. Best effort:
    /// a failure only means the Lease expires on its own, as before. Returns whether it was released.</summary>
    public static async Task<bool> ReleaseAsync(ILock leaseLock, ILogger logger, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var record = await leaseLock.GetAsync(cts.Token).ConfigureAwait(false);
            if (record?.HolderIdentity != leaseLock.Identity)
                return false;

            var now = DateTime.UtcNow;
            // The holder is emptied, never nulled: the KubernetesClient elector reads a null holder as
            // a missing Lease and tries to CREATE it, which fails for ever on the existing object. Same
            // record as client-go's ReleaseOnCancel.
            var released = await leaseLock.UpdateAsync(new LeaderElectionRecord
            {
                HolderIdentity = "",
                LeaseDurationSeconds = 1,
                AcquireTime = now,
                RenewTime = now,
                LeaderTransitions = record.LeaderTransitions,
            }, cts.Token).ConfigureAwait(false);

            if (released)
                logger.LogInformation("Released the Lease {Lease} ({Identity})", leaseLock.Describe(), leaseLock.Identity);
            else
                logger.LogWarning("Could not release the Lease {Lease}; it expires on its own", leaseLock.Describe());
            return released;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not release the Lease {Lease}; it expires on its own", leaseLock.Describe());
            return false;
        }
    }
}
