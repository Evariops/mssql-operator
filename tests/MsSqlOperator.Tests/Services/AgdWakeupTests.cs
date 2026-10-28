using k8s.Models;
using MsSqlOperator.Core.State;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The transitions that concern the databases of an AG wake them at once.</summary>
public class AgdWakeupTests
{
    private static SqlAvailabilityGroupDatabase Agd(string name, string ag) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "db" },
        Spec = new SqlAvailabilityGroupDatabaseSpec { AvailabilityGroupRef = new ResourceReference { Name = ag }, DatabaseName = name },
    };

    [Theory]
    [InlineData(TransitionKind.DatabaseSuspended, true)]
    [InlineData(TransitionKind.PrimaryChanged, true)]
    [InlineData(TransitionKind.ReplicaConnected, false)]
    public async Task OnlyTheDatabasesOfThatAg_AndOnlyForTheTransitionsThatConcernThem(TransitionKind kind, bool wakes)
    {
        var k8s = new FakeKubernetesClient();
        k8s.SetList<SqlAvailabilityGroupDatabase>([Agd("orders", "sales"), Agd("hr", "people")]);
        var runtime = new FakeEntityRuntime("SqlAvailabilityGroupDatabase");

        await AgdWakeup.WakeAsync(k8s, [runtime], "db", "sales", [new Transition(kind, "sales-0", "a", "b")], CancellationToken.None);

        Assert.Equal(wakes ? ["db/orders"] : [], runtime.EnqueueCalls);
    }
}
