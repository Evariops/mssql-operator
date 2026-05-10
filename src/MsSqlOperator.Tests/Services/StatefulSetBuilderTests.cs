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
}
