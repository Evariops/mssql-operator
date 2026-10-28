using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// The transitions of an AG that concern its databases (primary changed, a database
/// suspended, resumed, or changing synchronization) wake the SqlAvailabilityGroupDatabases of that
/// AG at once — their periodic passes are a safety net. Shared by the AG controller's health read and
/// the level-3 supervisor.
/// </summary>
public static class AgdWakeup
{
    public static bool Concerns(IReadOnlyList<Core.State.Transition> transitions)
        => transitions.Any(t => t.Kind is Core.State.TransitionKind.PrimaryChanged or Core.State.TransitionKind.DatabaseSuspended
            or Core.State.TransitionKind.DatabaseResumed or Core.State.TransitionKind.DatabaseSynchronizationChanged);

    public static async Task WakeAsync(IKubernetesClient client, IEnumerable<IEntityRuntime>? runtimes, string ns, string ag,
        IReadOnlyList<Core.State.Transition> transitions, CancellationToken ct)
    {
        if (!Concerns(transitions))
            return;
        var agdRuntime = runtimes?.FirstOrDefault(r => r.EntityKind == "SqlAvailabilityGroupDatabase");
        if (agdRuntime is null)
            return;
        foreach (var agd in await client.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct))
            if (agd.Spec.AvailabilityGroupRef.Name == ag)
                agdRuntime.Enqueue($"{ns}/{agd.Metadata.Name}");
    }
}
