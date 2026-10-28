using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

/// <summary>
/// What the generated manifests guarantee, whatever the spec — the properties the
/// failover, the rollout and the routing rely on. A change here is a decision, not a side effect.
/// </summary>
public class ManifestConformanceTests
{
    private readonly StatefulSetBuilder _builder = new(new AntiAffinityBuilder(), new TopologySpreadBuilder());

    private static SqlAvailabilityGroup Ag() => new()
    {
        Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "db", Uid = "uid" },
        Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
    };

    private static SqlInstance Instance() => new()
    {
        Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "db", Uid = "uid" },
        Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer" },
    };

    public static TheoryData<string> Kinds => ["ag", "instance"];

    private V1StatefulSet Build(string kind) => kind == "ag" ? _builder.BuildForAg(Ag(), "hash") : _builder.Build(Instance(), "hash");

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Selector_IsTheIdentityOnly_AndTheTemplateCarriesIt(string kind)
    {
        // The selector is immutable; anything else in it makes a later change impossible.
        var sts = Build(kind);
        var selector = sts.Spec.Selector.MatchLabels;

        Assert.Single(selector);
        Assert.All(selector, kv => Assert.Equal(kv.Value, sts.Spec.Template.Metadata.Labels[kv.Key]));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Pod_HasItsProbes_ItsPreStop_AndTimeToCheckpoint(string kind)
    {
        var pod = Build(kind).Spec.Template.Spec;
        var sql = pod.Containers.Single();

        Assert.NotNull(sql.ReadinessProbe);
        Assert.NotNull(sql.StartupProbe);
        Assert.NotNull(sql.LivenessProbe);
        Assert.NotNull(sql.Lifecycle?.PreStop);
        // SQL Server needs time to checkpoint; the 30 s default SIGKILLs it mid-checkpoint.
        Assert.True(pod.TerminationGracePeriodSeconds >= 60);
        // The liveness kill must not take the whole grace period (a slow shutdown is not a hang).
        Assert.True(sql.LivenessProbe.TerminationGracePeriodSeconds is null
                    || sql.LivenessProbe.TerminationGracePeriodSeconds <= pod.TerminationGracePeriodSeconds);
    }

    [Fact]
    public void Ag_PodsAreReachedThroughTheHeadlessService()
    {
        var sts = Build("ag");

        Assert.Equal("sales-headless", sts.Spec.ServiceName);
        Assert.Equal(3, sts.Spec.Replicas);
        Assert.Contains(sts.Spec.Template.Metadata.Labels, kv => kv.Key == "mssql-operator.evariops.com/ag" && kv.Value == "sales");
    }

    [Fact]
    public void Ag_PodManagementPolicy_IsTheDefault_UntilLot19ChangesItOnPurpose()
        // Immutable on a StatefulSet: changing it means recreating the StatefulSet around its pods.
        => Assert.True(Build("ag").Spec.PodManagementPolicy is null or "OrderedReady");

    [Fact]
    public void ListenerService_SelectsThePrimaryOnly()
    {
        var service = new ServiceBuilder().Build("sales-listener", "db",
            new Dictionary<string, string> { ["mssql-operator.evariops.com/ag"] = "sales", ["mssql-operator.evariops.com/role"] = "primary" },
            new ServiceSpec(), null);

        Assert.Equal("primary", service.Spec.Selector["mssql-operator.evariops.com/role"]);
        Assert.Equal("sales", service.Spec.Selector["mssql-operator.evariops.com/ag"]);
    }
}
