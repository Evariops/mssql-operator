using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The AG supervisor's actors.</summary>
public class AgSupervisorTests
{
    private readonly FakeKubernetesClient _k8s = new();

    private static SqlAvailabilityGroup Ag(string? level) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "sales", NamespaceProperty = "db",
            Annotations = level is null ? null : new Dictionary<string, string> { [MsSqlOperator.Core.Safety.SafetyLevel.Annotation] = level },
        },
        Spec = new SqlAvailabilityGroupSpec { SaPasswordSecretRef = new SecretReference { Name = "sa", Key = "password" } },
        Status = new SqlAvailabilityGroupStatus { PrimaryReplica = "sales-0" },
    };

    private AgSupervisor Supervisor(int ceiling)
        => new(_k8s, new AgStateReader(new FakeSqlConnectionFactory(), new FakeSqlAgManager()), new AgStateStore(),
            new OperatorOptions { SafetyLevelMax = ceiling }, null, new MsSqlOperator.Operator.Runtime.RuntimeLiveness(),
            NullLogger<AgSupervisor>.Instance);

    [Fact]
    public async Task BelowLevel3_NoActor()
    {
        using var supervisor = Supervisor(ceiling: 1);
        await supervisor.StartAsync(CancellationToken.None);

        supervisor.Signal(Ag(null), "reconcile");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_k8s.GetCalls, c => c.Type == nameof(SqlAvailabilityGroup));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ABurstOfSignals_IsCoalesced()
    {
        var ag = Ag("2"); // level 2 in force, level 3 in Shadow under a ceiling of 3
        _k8s.SetGet("sales", "db", ag);
        _k8s.SetGet("sa", "db", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "pw"u8.ToArray() } });
        using var supervisor = Supervisor(ceiling: 3);
        await supervisor.StartAsync(CancellationToken.None);

        for (var i = 0; i < 50; i++)
            supervisor.Signal(ag, "pod-event");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var evaluations = _k8s.GetCalls.Count(c => c.Type == nameof(SqlAvailabilityGroup));
        Assert.InRange(evaluations, 1, 3);
        await supervisor.StopAsync(CancellationToken.None);
    }
}
