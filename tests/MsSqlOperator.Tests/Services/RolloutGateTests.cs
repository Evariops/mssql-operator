using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>What counts as a restart for the fleet rollout budget.</summary>
public class RolloutGateTests
{
    private readonly FakeRolloutBudget _budget = new() { Allowed = false };
    private readonly FakeEventPublisher _events = new();
    private readonly List<ConditionStatus> _conditions = [];
    private readonly SqlInstance _owner = new() { Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default" } };

    private static V1StatefulSet StatefulSet(string image, Dictionary<string, string>? annotations = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Annotations = annotations },
        Spec = new V1StatefulSetSpec
        {
            Template = new V1PodTemplateSpec
            {
                Spec = new V1PodSpec { Containers = [new V1Container { Name = "mssql", Image = image }] },
            },
        },
    };

    private Task<bool> ApplyAsync(V1StatefulSet desired, V1StatefulSet? live) =>
        RolloutGate.ApplyAsync(desired, live, new SqlImageSelection("2022", "Developer"), null, onDelete: false,
            _conditions, _owner, _budget, _events, new OperatorOptions(), CancellationToken.None);

    [Fact]
    public async Task LiveWithoutHash_TemplateUnchanged_IsAdoptedWithoutTheBudget()
    {
        // A StatefulSet created before the hash annotation existed used to count as a pending
        // restart: with the budget spent, its whole apply was skipped, template change or not.
        _budget.TemplateChanges = false;
        var desired = StatefulSet("ignored");

        var applied = await ApplyAsync(desired, StatefulSet("ignored"));

        Assert.True(applied);
        Assert.Equal(0, _budget.MayStartCalls);
        Assert.Contains(_conditions, c => c is { Type: "RestartDeferred", Status: "False" });
        Assert.True(desired.Metadata.Annotations!.ContainsKey(RolloutBudget.TemplateHashAnnotation));
    }

    [Fact]
    public async Task LiveWithoutHash_TemplateChanged_WaitsForTheBudget()
    {
        _budget.TemplateChanges = true;

        var applied = await ApplyAsync(StatefulSet("ignored"), StatefulSet("ignored"));

        Assert.False(applied);
        Assert.Contains(_conditions, c => c is { Type: "RestartDeferred", Status: "True" });
    }

    [Fact]
    public async Task LiveWithTheSameHash_IsNotARestart()
    {
        var desired = StatefulSet("ignored");
        await ApplyAsync(desired, null); // stamps the hash of the desired template
        var hash = desired.Metadata.Annotations![RolloutBudget.TemplateHashAnnotation];
        _budget.TemplateChanges = true;

        var applied = await ApplyAsync(StatefulSet("ignored"),
            StatefulSet("ignored", new Dictionary<string, string> { [RolloutBudget.TemplateHashAnnotation] = hash }));

        Assert.True(applied);
        Assert.Equal(0, _budget.MayStartCalls);
    }

    [Fact]
    public async Task ChangesTemplate_ComparesTheDryRunWithTheLiveObject()
    {
        var k8s = new FakeKubernetesClient();
        var budget = new RolloutBudget(k8s, new OperatorOptions());
        var live = StatefulSet("mcr.microsoft.com/mssql/server:2022-CU20");

        k8s.DryRunApplyResult = _ => StatefulSet("mcr.microsoft.com/mssql/server:2022-CU20");
        Assert.False(await budget.ChangesTemplateAsync(StatefulSet("x"), live, CancellationToken.None));

        k8s.DryRunApplyResult = _ => StatefulSet("mcr.microsoft.com/mssql/server:2022-CU21");
        Assert.True(await budget.ChangesTemplateAsync(StatefulSet("x"), live, CancellationToken.None));
    }

    [Fact]
    public async Task ChangesTemplate_DryRunRejected_CountsAsARestart()
    {
        var k8s = new FakeKubernetesClient
        {
            DryRunApplyResult = _ => throw new k8s.Autorest.HttpOperationException("422 immutable field"),
        };
        var budget = new RolloutBudget(k8s, new OperatorOptions());

        Assert.True(await budget.ChangesTemplateAsync(StatefulSet("x"), StatefulSet("x"), CancellationToken.None));
    }
}
