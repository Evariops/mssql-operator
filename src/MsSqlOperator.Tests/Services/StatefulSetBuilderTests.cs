using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class StatefulSetBuilderTests
{
    private readonly StatefulSetBuilder _builder;

    public StatefulSetBuilderTests()
    {
        _builder = new StatefulSetBuilder(new AntiAffinityBuilder());
    }

    [Fact]
    public void Build_SingleNode_CreatesStatefulSetWithOneReplica()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta
            {
                Name = "my-sql",
                NamespaceProperty = "default",
                Uid = "uid-1"
            },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec { Size = "10Gi" },
                    Log = new StorageVolumeSpec { Size = "5Gi" }
                }
            }
        };

        var sts = _builder.Build(instance, "P@ssw0rd!");

        sts.Should().NotBeNull();
        sts.Metadata.Name.Should().Be("my-sql");
        sts.Metadata.NamespaceProperty.Should().Be("default");
        sts.Spec.Replicas.Should().Be(1);
        sts.Spec.Template.Spec.Containers.Should().ContainSingle();

        var container = sts.Spec.Template.Spec.Containers[0];
        container.Image.Should().Contain("mssql");
    }

    [Fact]
    public void Build_WithResources_SetsResourceRequirements()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "res-sql", NamespaceProperty = "default", Uid = "uid-2" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    Requests = new Entities.Common.ResourceQuantity { Cpu = "500m", Memory = "2Gi" },
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "2", Memory = "4Gi" }
                },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, "P@ssw0rd!");
        var container = sts.Spec.Template.Spec.Containers[0];

        container.Resources.Should().NotBeNull();
        container.Resources.Requests.Should().ContainKey("cpu");
        container.Resources.Requests.Should().ContainKey("memory");
    }

    [Fact]
    public void BuildForAg_CreatesStatefulSetWithMultipleReplicas()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default", Uid = "uid-3" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Edition = "Enterprise",
                Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
                SaPasswordSecretRef = new SecretReference { Name = "secret", Key = "password" },
                InstanceTemplate = new AgInstanceTemplateSpec
                {
                    Version = "2022",
                    Storage = new StorageSpec
                    {
                        Data = new StorageVolumeSpec { Size = "20Gi" },
                        Log = new StorageVolumeSpec { Size = "10Gi" }
                    }
                }
            }
        };

        var sts = _builder.BuildForAg(ag, "P@ssw0rd!");

        sts.Should().NotBeNull();
        sts.Spec.Replicas.Should().Be(3);
        sts.Spec.ServiceName.Should().Contain("my-ag");
    }

    [Fact]
    public void Build_WithMemorySpec_SetsEnvVarAndOverridesResources()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "mem-sql", NamespaceProperty = "default", Uid = "uid-mem" },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "8Gi", SystemReserve = "2Gi" },
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec { Size = "10Gi" }
                }
            }
        };

        var sts = _builder.Build(instance, "P@ssw0rd!");
        var container = sts.Spec.Template.Spec.Containers[0];

        // MSSQL_MEMORY_LIMIT_MB should be set to total - reserve = 6144
        container.Env.Should().Contain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "6144");

        // Pod memory requests AND limits should both equal total (8Gi) for Guaranteed QoS
        container.Resources.Requests.Should().ContainKey("memory");
        container.Resources.Limits.Should().ContainKey("memory");
        container.Resources.Requests["memory"].Should().Be(new k8s.Models.ResourceQuantity("8Gi"));
        container.Resources.Limits["memory"].Should().Be(new k8s.Models.ResourceQuantity("8Gi"));
    }

    [Fact]
    public void Build_WithoutMemorySpec_DoesNotSetMssqlMemoryEnv()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "nomem-sql", NamespaceProperty = "default", Uid = "uid-nomem" },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, "P@ssw0rd!");
        var container = sts.Spec.Template.Spec.Containers[0];

        container.Env.Should().NotContain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB");
    }

    [Fact]
    public void BuildForAg_WithMemorySpec_SetsEnvVarOnAllReplicas()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "mem-ag", NamespaceProperty = "default", Uid = "uid-memag" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Edition = "Enterprise",
                Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
                SaPasswordSecretRef = new SecretReference { Name = "secret", Key = "password" },
                InstanceTemplate = new AgInstanceTemplateSpec
                {
                    Version = "2022",
                    Memory = new MemorySpec { Total = "16Gi", SystemReserve = "2Gi" },
                    Storage = new StorageSpec
                    {
                        Data = new StorageVolumeSpec { Size = "20Gi" },
                        Log = new StorageVolumeSpec { Size = "10Gi" }
                    }
                }
            }
        };

        var sts = _builder.BuildForAg(ag, "P@ssw0rd!");
        var container = sts.Spec.Template.Spec.Containers[0];

        // 16Gi - 2Gi = 14336 MB
        container.Env.Should().Contain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "14336");
        container.Resources.Requests["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
        container.Resources.Limits["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
    }
}
