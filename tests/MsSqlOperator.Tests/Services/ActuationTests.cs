using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.ViewApi;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The actuation gateway and its policy.</summary>
public class ActuationTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeEventPublisher _events = new();
    private readonly PlanStore _plans = new();
    private readonly ActuationGateway _gateway;

    public ActuationTests()
        => _gateway = new ActuationGateway(new ActuationPolicy(new AutomationOptions(), _clock), _plans, _k8s, _events, clock: _clock);

    private static SqlAvailabilityGroup Ag(string? mode = null, params string[] preAuthorized) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default", Generation = 3 },
        Spec = new SqlAvailabilityGroupSpec { Automation = new AutomationSpec { Mode = mode ?? "Apply", PreAuthorized = [.. preAuthorized] } },
    };

    private static ActionRequest Drop(string replica = "ag-1") => new(ActionType.RemoveDatabaseFromAg, replica, "3", "remove OrdersDB", "OrdersDB");

    [Fact]
    public async Task DestructiveAction_ByTheOperator_WaitsForAnApproval_ThenRunsOnceApprovedAndConsumesIt()
    {
        var ag = Ag();
        var ran = 0;

        var withheld = await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(ag, Drop(), _ => { ran++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(DecisionKind.RequiresApproval, withheld.Decision.Kind);
        Assert.Contains("ActionRequiresApproval", _events.Reasons);
        Assert.Equal(0, ran);

        ag.Metadata.Annotations = new Dictionary<string, string> { [ActuationGateway.ApproveAnnotation] = Drop().Id };
        await _gateway.RunAsync(ag, Drop(), _ => { ran++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, ran);
        Assert.Contains(_k8s.PatchMetadataCalls, c => c.Annotations[ActuationGateway.ApproveAnnotation] is null);
        Assert.Contains(_plans.Get("SqlAvailabilityGroup/default/ag"), p => p.Executed && p.Decision == "Approved");
    }

    [Theory]
    [InlineData("Observe")]
    [InlineData("Plan")]
    public async Task ObserveOrPlan_NothingRuns_ThePlanSaysWhatWouldHave(string mode)
    {
        var ran = false;

        await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(Ag(mode),
            new ActionRequest(ActionType.Failover, "ag-1", "3", "fail over"), _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None));

        Assert.False(ran);
        Assert.Contains(_plans.Get("SqlAvailabilityGroup/default/ag"), p => p is { Decision: "Withheld", Executed: false });
    }

    [Fact]
    public async Task PreAuthorized_RunsOnceAnHour()
    {
        var ag = Ag(null, "RemoveDatabaseFromAg");

        await _gateway.RunAsync(ag, Drop("a"), _ => Task.CompletedTask, CancellationToken.None);
        var second = await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(ag, Drop("b"), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(DecisionKind.RequiresApproval, second.Decision.Kind);

        _clock.Advance(TimeSpan.FromHours(1));
        await _gateway.RunAsync(ag, Drop("c"), _ => Task.CompletedTask, CancellationToken.None);
    }

    [Theory]
    [InlineData(ActuationIntent.ResourceDeletion)]
    [InlineData(ActuationIntent.SpecDeclared)]
    [InlineData(ActuationIntent.HumanAnnotation)]
    public async Task DestructiveAction_WantedByAHuman_Runs(ActuationIntent intent)
    {
        var ran = false;
        await _gateway.RunAsync(Ag(), Drop(), _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None, intent);
        Assert.True(ran);
    }

    [Fact]
    public async Task RegeneratingTheServiceMasterKey_IsNeverAutomatic()
    {
        var ag = Ag(null, "RegenerateServiceMasterKey");
        ag.Metadata.Annotations = new Dictionary<string, string> { [ActuationGateway.ApproveAnnotation] = "anything" };

        var denied = await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(ag,
            new ActionRequest(ActionType.RegenerateServiceMasterKey, "ag-0", "3", "regenerate"), _ => Task.CompletedTask,
            CancellationToken.None, ActuationIntent.SpecDeclared));

        Assert.Equal(DecisionKind.Denied, denied.Decision.Kind);
    }

    [Fact]
    public async Task PreconditionNoLongerHolding_DeniesTheAction()
    {
        var ran = false;
        await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(Ag(), new ActionRequest(ActionType.Failover, "ag-1", "3", "fail over"),
            _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None, precondition: _ => Task.FromResult<string?>("ag-1 lags")));
        Assert.False(ran);
    }

    [Fact]
    public async Task OperatorCeiling_CapsEveryResource()
    {
        var gateway = new ActuationGateway(new ActuationPolicy(new AutomationOptions { MaxMode = AutomationMode.Observe }, _clock), _plans);
        await Assert.ThrowsAsync<ActionWithheldException>(() => gateway.RunAsync(Ag("Apply"),
            new ActionRequest(ActionType.Failover, "ag-1", "3", "fail over"), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public void ActionId_IsDeterministic()
    {
        Assert.Equal(Drop().Id, Drop().Id);
        Assert.StartsWith("RemoveDatabaseFromAg:ag-1:3:", Drop().Id);
        Assert.NotEqual(Drop("ag-1").Id, new ActionRequest(ActionType.RemoveDatabaseFromAg, "ag-1", "3", "x", "OtherDb").Id);
    }

    [Fact]
    public async Task OperatorPlans_ListsTheDecisions()
    {
        await Assert.ThrowsAsync<ActionWithheldException>(() => _gateway.RunAsync(Ag(), Drop(), _ => Task.CompletedTask, CancellationToken.None));
        var provider = new OperatorPlansProvider(_plans);

        var items = await provider.ListAsync("default", null, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("RequiresApproval", item["decision"]);
        Assert.Equal(Drop().Id, item["id"]);
        Assert.Empty(await provider.ListAsync("other", null, CancellationToken.None));
    }
}
