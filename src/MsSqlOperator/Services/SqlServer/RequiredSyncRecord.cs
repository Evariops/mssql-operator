using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// status.lastRequiredSynchronizedSecondaries is what a failover without the primary reads to know
/// whether the primary could commit alone. It was refreshed from the primary at the start of each
/// pass, and the operator lowered the setting later in the pass: when the status write of that pass
/// failed, or the operator stopped in between, the status still said 1 while the primary committed
/// alone, and a later failover could be called lossless. The lower value is written first.
/// </summary>
public static class RequiredSyncRecord
{
    /// <summary>Records <paramref name="lowered"/> before REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT is
    /// lowered to it. Throws when it cannot be written: the setting must then not be lowered.</summary>
    public static async Task BeforeLoweringAsync(IKubernetesClient client, SqlAvailabilityGroup ag, int lowered, CancellationToken ct)
    {
        ag.Status ??= new SqlAvailabilityGroupStatus();
        if (ag.Status.LastRequiredSynchronizedSecondaries is { } recorded && recorded <= lowered)
            return;
        await client.PatchStatusAsync(ag, new Dictionary<string, object?> { ["lastRequiredSynchronizedSecondaries"] = lowered }, ct);
        ag.Status.LastRequiredSynchronizedSecondaries = lowered;
    }
}
