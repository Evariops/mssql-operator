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
        _builder = new StatefulSetBuilder(new AntiAffinityBuilder(), new TopologySpreadBuilder());
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

        var sts = _builder.Build(instance, null);

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

        var sts = _builder.Build(instance, null);
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

        var sts = _builder.BuildForAg(ag, null);

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

        var sts = _builder.Build(instance, null);
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

        var sts = _builder.Build(instance, null);
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

        var sts = _builder.BuildForAg(ag, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // 16Gi - 2Gi = 14336 MB
        container.Env.Should().Contain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "14336");
        container.Resources.Requests["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
        container.Resources.Limits["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
    }

    [Fact]
    public void Build_WithSaPasswordSecretRef_UsesSecretKeyRef()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "sec-sql", NamespaceProperty = "default", Uid = "uid-sec" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        var saEnv = container.Env.First(e => e.Name == "MSSQL_SA_PASSWORD");
        saEnv.Value.Should().BeNull();
        saEnv.ValueFrom.Should().NotBeNull();
        saEnv.ValueFrom.SecretKeyRef.Name.Should().Be("sa-secret");
        saEnv.ValueFrom.SecretKeyRef.Key.Should().Be("password");
    }

    [Fact]
    public void Build_WithConfigHash_SetsAnnotation()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "hash-sql", NamespaceProperty = "default", Uid = "uid-hash" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, "abc123hash");
        sts.Spec.Template.Metadata.Annotations.Should().ContainKey("mssql-operator.evariops.com/config-hash");
        sts.Spec.Template.Metadata.Annotations["mssql-operator.evariops.com/config-hash"].Should().Be("abc123hash");
    }

    [Fact]
    public void Build_WithoutConfigHash_NoAnnotation()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "nohash-sql", NamespaceProperty = "default", Uid = "uid-nohash" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        sts.Spec.Template.Metadata.Annotations.Should().BeNull();
    }

    [Fact]
    public void Build_WithTempDbAndSysDb_CreatesAdditionalPVCs()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "vol-sql", NamespaceProperty = "default", Uid = "uid-vol" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec { Size = "20Gi" },
                    Log = new StorageVolumeSpec { Size = "10Gi" },
                    Backup = new StorageVolumeSpec { Size = "10Gi" },
                    SysDb = new StorageVolumeSpec { Size = "5Gi" },
                    TempDb = new StorageVolumeSpec { Size = "15Gi", StorageClassName = "local-ssd" },
                    ErrorLogs = new StorageVolumeSpec { Size = "2Gi" },
                    CertBackups = new StorageVolumeSpec { Size = "1Gi" }
                }
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.VolumeClaimTemplates.Should().HaveCount(7);
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "data");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "log");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "backup");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "sysdb");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "tempdb");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "errorlogs");
        sts.Spec.VolumeClaimTemplates.Should().Contain(c => c.Metadata.Name == "certbackups");

        // TempDb should have custom storage class
        var tempDbClaim = sts.Spec.VolumeClaimTemplates.First(c => c.Metadata.Name == "tempdb");
        tempDbClaim.Spec.StorageClassName.Should().Be("local-ssd");

        // Container should have volume mounts for all volumes
        var container = sts.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().Contain(m => m.Name == "sysdb" && m.MountPath == "/var/opt/mssql/sysdb");
        container.VolumeMounts.Should().Contain(m => m.Name == "tempdb" && m.MountPath == "/var/opt/mssql/tempdb");
        container.VolumeMounts.Should().Contain(m => m.Name == "errorlogs" && m.MountPath == "/var/opt/mssql/errorlogs");
        container.VolumeMounts.Should().Contain(m => m.Name == "certbackups" && m.MountPath == "/var/opt/mssql/tde-backups");
    }

    [Fact]
    public void Build_WithNodeSelectorAndTolerations_AppliesSchedulingFields()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "sched-sql", NamespaceProperty = "default", Uid = "uid-sched" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } },
                NodeSelector = new Dictionary<string, string> { { "disktype", "ssd" } },
                Tolerations = [new V1Toleration { Key = "mssql", OperatorProperty = "Exists", Effect = "NoSchedule" }],
                PriorityClassName = "high-priority"
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.Template.Spec.NodeSelector.Should().ContainKey("disktype");
        sts.Spec.Template.Spec.Tolerations.Should().HaveCount(1);
        sts.Spec.Template.Spec.PriorityClassName.Should().Be("high-priority");
    }

    [Fact]
    public void Build_DefaultSecurityContext_IsApplied()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "sec-sql", NamespaceProperty = "default", Uid = "uid-secctx" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);

        // Pod security context defaults
        sts.Spec.Template.Spec.SecurityContext.Should().NotBeNull();
        sts.Spec.Template.Spec.SecurityContext.RunAsNonRoot.Should().BeTrue();
        sts.Spec.Template.Spec.SecurityContext.RunAsUser.Should().Be(10001);
        sts.Spec.Template.Spec.SecurityContext.FsGroup.Should().Be(10001);

        // Container security context defaults: must let the stock SQL Server image start.
        // Dropping ALL caps / forbidding privilege escalation breaks sqlservr ("Operation not permitted").
        var container = sts.Spec.Template.Spec.Containers[0];
        container.SecurityContext.Should().NotBeNull();
        container.SecurityContext.AllowPrivilegeEscalation.Should().BeTrue();
        // Read-only root filesystem is the default.
        container.SecurityContext.ReadOnlyRootFilesystem.Should().BeTrue();
    }

    [Fact]
    public void Build_ReadOnlyRootFilesystem_AddsEmptyDirMounts()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "ro-sql", NamespaceProperty = "default", Uid = "uid-ro" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                // Opt into a read-only root filesystem; the operator must then provide writable emptyDirs.
                SecurityContext = new V1SecurityContext { ReadOnlyRootFilesystem = true },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // emptyDir mounts for readOnlyRootFilesystem
        container.VolumeMounts.Should().Contain(m => m.Name == "tmp" && m.MountPath == "/tmp");
        container.VolumeMounts.Should().Contain(m => m.Name == "mssql-system" && m.MountPath == "/var/opt/mssql/.system");
        // sqlservr creates /var/opt/mssql/secrets on startup and aborts under a read-only root
        // without a writable mount there.
        container.VolumeMounts.Should().Contain(m => m.Name == "mssql-secrets" && m.MountPath == "/var/opt/mssql/secrets");

        // ConfigMap mount for mssql.conf
        container.VolumeMounts.Should().Contain(m => m.Name == "mssql-conf" && m.MountPath == "/var/opt/mssql/mssql.conf");

        // Volumes
        sts.Spec.Template.Spec.Volumes.Should().Contain(v => v.Name == "tmp" && v.EmptyDir != null);
        sts.Spec.Template.Spec.Volumes.Should().Contain(v => v.Name == "mssql-system" && v.EmptyDir != null);
        sts.Spec.Template.Spec.Volumes.Should().Contain(v => v.Name == "mssql-secrets" && v.EmptyDir != null);
        sts.Spec.Template.Spec.Volumes.Should().Contain(v => v.Name == "mssql-conf" && v.ConfigMap != null);
    }

    [Fact]
    public void Build_WithMachineKey_MountsWritableSystemEmptyDir()
    {
        // Even with the default (writable) root filesystem, a mounted machine-key requires the
        // .system emptyDir so its subPath mount does not leave .system root-owned (regression:
        // SQL Server failed with "Directory [/var/opt/mssql/.system/system] could not be created").
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "mk-sql", NamespaceProperty = "default", Uid = "uid-mk" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null, machineKeySecretName: "mk-sql-machine-key");
        var container = sts.Spec.Template.Spec.Containers[0];

        // Root filesystem is read-only by default, so /tmp is an emptyDir too.
        container.SecurityContext.ReadOnlyRootFilesystem.Should().BeTrue();
        container.VolumeMounts.Should().Contain(m => m.Name == "tmp" && m.MountPath == "/tmp");
        container.VolumeMounts.Should().Contain(m => m.Name == "mssql-system" && m.MountPath == "/var/opt/mssql/.system");
        container.VolumeMounts.Should().Contain(m => m.Name == "machine-key" && m.MountPath == "/var/opt/mssql/.system/machine-key");
        sts.Spec.Template.Spec.Volumes.Should().Contain(v => v.Name == "mssql-system" && v.EmptyDir != null);
    }

    [Fact]
    public void Build_WithCustomAccessModes_AppliesThemToPVC()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "am-sql", NamespaceProperty = "default", Uid = "uid-am" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec
                    {
                        Size = "10Gi",
                        AccessModes = ["ReadWriteMany"]
                    }
                }
            }
        };

        var sts = _builder.Build(instance, null);
        var dataClaim = sts.Spec.VolumeClaimTemplates.First(c => c.Metadata.Name == "data");
        dataClaim.Spec.AccessModes.Should().Contain("ReadWriteMany");
    }

    [Fact]
    public void Build_Probes_FollowI4Contract()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "probe-sql", NamespaceProperty = "default", Uid = "uid-probe" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // Readiness requires a real login (exec sqlcmd SELECT 1); the command only
        // references the env var — process listings never show the secret value.
        container.ReadinessProbe.Exec.Should().NotBeNull();
        var script = string.Join(" ", container.ReadinessProbe.Exec.Command);
        script.Should().Contain("$MSSQL_SA_PASSWORD");
        script.Should().Contain("SELECT 1");

        // Startup probe gives CU upgrade scripts a 15-minute budget before liveness applies.
        container.StartupProbe.Should().NotBeNull();
        (container.StartupProbe.FailureThreshold * container.StartupProbe.PeriodSeconds).Should().BeGreaterThanOrEqualTo(900);

        // Liveness stays TCP with a tolerant threshold (no restart storms under load).
        container.LivenessProbe.TcpSocket.Should().NotBeNull();
        container.LivenessProbe.Exec.Should().BeNull();
        container.LivenessProbe.FailureThreshold.Should().BeGreaterThanOrEqualTo(5);
    }

    // ── RuntimeClassName tests ──────────────────────────────

    [Fact]
    public void Build_WithRuntimeClassName_SetsOnPodSpec()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "kata-sql", NamespaceProperty = "default", Uid = "uid-kata" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                RuntimeClassName = "kata",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.Template.Spec.RuntimeClassName.Should().Be("kata");
    }

    [Fact]
    public void Build_WithoutRuntimeClassName_DoesNotSetOnPodSpec()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "std-sql", NamespaceProperty = "default", Uid = "uid-std" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.Template.Spec.RuntimeClassName.Should().BeNull();
    }

    [Fact]
    public void Build_WithKataAndMemory_SubtractsKataOverhead()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "kata-mem-sql", NamespaceProperty = "default", Uid = "uid-kata-mem" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                RuntimeClassName = "kata",
                Memory = new MemorySpec { Total = "32Gi", SystemReserve = "2Gi" },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // 32768 - 2048 - 150 (kata overhead) = 30570
        container.Env.Should().Contain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "30570");
    }

    [Fact]
    public void Build_WithoutKataAndMemory_NoKataOverhead()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "std-mem-sql", NamespaceProperty = "default", Uid = "uid-std-mem" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "32Gi", SystemReserve = "2Gi" },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // 32768 - 2048 = 30720 (no kata overhead)
        container.Env.Should().Contain(e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "30720");
    }

    // ── CpuPinning tests ────────────────────────────────────

    [Fact]
    public void Build_WithCpuPinning_EqualizesCpuRequestsAndLimits()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "pin-sql", NamespaceProperty = "default", Uid = "uid-pin" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        container.Resources.Requests["cpu"].Should().Be(new k8s.Models.ResourceQuantity("4"));
        container.Resources.Limits["cpu"].Should().Be(new k8s.Models.ResourceQuantity("4"));
    }

    [Fact]
    public void Build_WithCpuPinningAndMemory_AchievesGuaranteedQoS()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "qos-sql", NamespaceProperty = "default", Uid = "uid-qos" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                },
                Memory = new MemorySpec { Total = "16Gi", SystemReserve = "2Gi" },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // CPU: requests == limits (Guaranteed)
        container.Resources.Requests["cpu"].Should().Be(new k8s.Models.ResourceQuantity("4"));
        container.Resources.Limits["cpu"].Should().Be(new k8s.Models.ResourceQuantity("4"));

        // Memory: requests == limits (Guaranteed)
        container.Resources.Requests["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
        container.Resources.Limits["memory"].Should().Be(new k8s.Models.ResourceQuantity("16Gi"));
    }

    [Fact]
    public void Build_WithoutCpuPinning_DoesNotEqualizeCpu()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "nopin-sql", NamespaceProperty = "default", Uid = "uid-nopin" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    Requests = new Entities.Common.ResourceQuantity { Cpu = "500m" },
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "2" }
                },
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];

        // Without cpuPinning, requests and limits can differ
        container.Resources.Requests["cpu"].Should().Be(new k8s.Models.ResourceQuantity("500m"));
        container.Resources.Limits["cpu"].Should().Be(new k8s.Models.ResourceQuantity("2"));
    }

    [Fact]
    public void BuildForAg_WithRuntimeClassName_SetsOnPodSpec()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "kata-ag", NamespaceProperty = "default", Uid = "uid-kata-ag" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Edition = "Enterprise",
                Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
                SaPasswordSecretRef = new SecretReference { Name = "secret", Key = "password" },
                InstanceTemplate = new AgInstanceTemplateSpec
                {
                    Version = "2022",
                    RuntimeClassName = "kata",
                    Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "20Gi" } }
                }
            }
        };

        var sts = _builder.BuildForAg(ag, null);

        sts.Spec.Template.Spec.RuntimeClassName.Should().Be("kata");
    }

    // ── Topology spread & pool ──────────────────────

    [Fact]
    public void BuildForAg_NoPlacementSpec_DefaultsToPreferredNodeAndZoneSelfSpread()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default", Uid = "uid-d" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Edition = "Enterprise",
                Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
                SaPasswordSecretRef = new SecretReference { Name = "secret", Key = "password" },
                InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022", Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "20Gi" } } }
            }
        };

        var sts = _builder.BuildForAg(ag, null);

        var spread = sts.Spec.Template.Spec.TopologySpreadConstraints;
        spread.Should().HaveCount(2);
        spread.Should().OnlyContain(c => c.WhenUnsatisfiable == "ScheduleAnyway");
        spread!.Select(c => c.TopologyKey).Should().BeEquivalentTo(["kubernetes.io/hostname", "topology.kubernetes.io/zone"]);
        // Selector matches the AG identity, never version/edition.
        spread.Should().OnlyContain(c => c.LabelSelector.MatchLabels.ContainsKey("mssql-operator.evariops.com/ag"));
        sts.Spec.Template.Spec.Affinity.Should().BeNull();
    }

    [Fact]
    public void BuildForAg_ExplicitAntiAffinity_SuppressesDefaultSpread()
    {
        var ag = new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default", Uid = "uid-aa" },
            Spec = new SqlAvailabilityGroupSpec
            {
                Edition = "Enterprise",
                Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
                SaPasswordSecretRef = new SecretReference { Name = "secret", Key = "password" },
                AntiAffinity = new AntiAffinitySpec { RequiredSelfAntiAffinity = new SelfAntiAffinityRule() },
                InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022", Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "20Gi" } } }
            }
        };

        var sts = _builder.BuildForAg(ag, null);

        sts.Spec.Template.Spec.TopologySpreadConstraints.Should().BeNull();
        sts.Spec.Template.Spec.Affinity!.PodAntiAffinity.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithPool_StampsPoolOnPodTemplateNotSelector()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "my-sql", NamespaceProperty = "default", Uid = "uid-p" },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Pool = "prod-critical",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.Template.Metadata.Labels.Should().ContainKey("mssql-operator.evariops.com/pool")
            .WhoseValue.Should().Be("prod-critical");
        // The immutable selector must NOT carry the pool label.
        sts.Spec.Selector.MatchLabels.Should().NotContainKey("mssql-operator.evariops.com/pool");
    }

    [Fact]
    public void Build_WithoutPool_SelectorAndTemplateLabelsUnchanged()
    {
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "my-sql", NamespaceProperty = "default", Uid = "uid-np" },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }
            }
        };

        var sts = _builder.Build(instance, null);

        sts.Spec.Template.Metadata.Labels.Should().NotContainKey("mssql-operator.evariops.com/pool");
        sts.Spec.Template.Spec.TopologySpreadConstraints.Should().BeNull();
    }
}
