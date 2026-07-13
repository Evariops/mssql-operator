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

        Assert.NotNull(sts);
        Assert.Equal("my-sql", sts.Metadata.Name);
        Assert.Equal("default", sts.Metadata.NamespaceProperty);
        Assert.Equal(1, sts.Spec.Replicas);
        Assert.Single(sts.Spec.Template.Spec.Containers);

        var container = sts.Spec.Template.Spec.Containers[0];
        Assert.Contains("mssql", container.Image);
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

        Assert.NotNull(container.Resources);
        Assert.Contains("cpu", container.Resources.Requests);
        Assert.Contains("memory", container.Resources.Requests);
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

        Assert.NotNull(sts);
        Assert.Equal(3, sts.Spec.Replicas);
        Assert.Contains("my-ag", sts.Spec.ServiceName);
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
        Assert.Contains(container.Env, e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "6144");

        // Pod memory requests AND limits should both equal total (8Gi) for Guaranteed QoS
        Assert.Contains("memory", container.Resources.Requests);
        Assert.Contains("memory", container.Resources.Limits);
        Assert.Equal(new k8s.Models.ResourceQuantity("8Gi"), container.Resources.Requests["memory"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("8Gi"), container.Resources.Limits["memory"]);
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

        Assert.DoesNotContain(container.Env, e => e.Name == "MSSQL_MEMORY_LIMIT_MB");
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
        Assert.Contains(container.Env, e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "14336");
        Assert.Equal(new k8s.Models.ResourceQuantity("16Gi"), container.Resources.Requests["memory"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("16Gi"), container.Resources.Limits["memory"]);
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
        Assert.Null(saEnv.Value);
        Assert.NotNull(saEnv.ValueFrom);
        Assert.Equal("sa-secret", saEnv.ValueFrom.SecretKeyRef.Name);
        Assert.Equal("password", saEnv.ValueFrom.SecretKeyRef.Key);
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
        Assert.Contains("mssql-operator.evariops.com/config-hash", sts.Spec.Template.Metadata.Annotations);
        Assert.Equal("abc123hash", sts.Spec.Template.Metadata.Annotations["mssql-operator.evariops.com/config-hash"]);
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
        Assert.Null(sts.Spec.Template.Metadata.Annotations);
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

        Assert.Equal(7, sts.Spec.VolumeClaimTemplates.Count());
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "data");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "log");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "backup");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "sysdb");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "tempdb");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "errorlogs");
        Assert.Contains(sts.Spec.VolumeClaimTemplates, c => c.Metadata.Name == "certbackups");

        // TempDb should have custom storage class
        var tempDbClaim = sts.Spec.VolumeClaimTemplates.First(c => c.Metadata.Name == "tempdb");
        Assert.Equal("local-ssd", tempDbClaim.Spec.StorageClassName);

        // Container should have volume mounts for all volumes
        var container = sts.Spec.Template.Spec.Containers[0];
        Assert.Contains(container.VolumeMounts, m => m.Name == "sysdb" && m.MountPath == "/var/opt/mssql/sysdb");
        Assert.Contains(container.VolumeMounts, m => m.Name == "tempdb" && m.MountPath == "/var/opt/mssql/tempdb");
        Assert.Contains(container.VolumeMounts, m => m.Name == "errorlogs" && m.MountPath == "/var/opt/mssql/errorlogs");
        Assert.Contains(container.VolumeMounts, m => m.Name == "certbackups" && m.MountPath == "/var/opt/mssql/tde-backups");
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

        Assert.Contains("disktype", sts.Spec.Template.Spec.NodeSelector);
        Assert.Single(sts.Spec.Template.Spec.Tolerations);
        Assert.Equal("high-priority", sts.Spec.Template.Spec.PriorityClassName);
    }

    [Fact]
    public void Build_PinsPodToAmd64_ByDefault_AndKeepsUserSelector()
    {
        // SQL Server is amd64-only; the pod must never land on arm64 in a mixed-arch cluster.
        var noSelector = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "arch-sql", NamespaceProperty = "default", Uid = "u" },
            Spec = new SqlInstanceSpec { Version = "2022", Edition = "Developer" }
        };
        var sts = _builder.Build(noSelector, null);
        Assert.Equal("amd64", sts.Spec.Template.Spec.NodeSelector["kubernetes.io/arch"]);

        // A user selector is preserved alongside the arch pin.
        var withSelector = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "arch-sql2", NamespaceProperty = "default", Uid = "u2" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                NodeSelector = new Dictionary<string, string> { ["disktype"] = "ssd" }
            }
        };
        var sts2 = _builder.Build(withSelector, null);
        Assert.Equal("amd64", sts2.Spec.Template.Spec.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal("ssd", sts2.Spec.Template.Spec.NodeSelector["disktype"]);
    }

    [Fact]
    public void Build_RespectsExplicitArchOverride()
    {
        var entity = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "arch-sql3", NamespaceProperty = "default", Uid = "u3" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                NodeSelector = new Dictionary<string, string> { ["kubernetes.io/arch"] = "arm64" }
            }
        };
        var sts = _builder.Build(entity, null);
        Assert.Equal("arm64", sts.Spec.Template.Spec.NodeSelector["kubernetes.io/arch"]);
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
        Assert.NotNull(sts.Spec.Template.Spec.SecurityContext);
        Assert.True(sts.Spec.Template.Spec.SecurityContext.RunAsNonRoot);
        Assert.Equal(10001, sts.Spec.Template.Spec.SecurityContext.RunAsUser);
        Assert.Equal(10001, sts.Spec.Template.Spec.SecurityContext.FsGroup);

        // Container security context defaults: must let the stock SQL Server image start.
        // Dropping ALL caps / forbidding privilege escalation breaks sqlservr ("Operation not permitted").
        var container = sts.Spec.Template.Spec.Containers[0];
        Assert.NotNull(container.SecurityContext);
        Assert.True(container.SecurityContext.AllowPrivilegeEscalation);
        // Read-only root filesystem is the default.
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
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
        Assert.Contains(container.VolumeMounts, m => m.Name == "tmp" && m.MountPath == "/tmp");
        Assert.Contains(container.VolumeMounts, m => m.Name == "mssql-system" && m.MountPath == "/var/opt/mssql/.system");
        // sqlservr creates /var/opt/mssql/secrets on startup and aborts under a read-only root
        // without a writable mount there.
        Assert.Contains(container.VolumeMounts, m => m.Name == "mssql-secrets" && m.MountPath == "/var/opt/mssql/secrets");

        // ConfigMap mount for mssql.conf
        Assert.Contains(container.VolumeMounts, m => m.Name == "mssql-conf" && m.MountPath == "/var/opt/mssql/mssql.conf");

        // Volumes
        Assert.Contains(sts.Spec.Template.Spec.Volumes, v => v.Name == "tmp" && v.EmptyDir != null);
        Assert.Contains(sts.Spec.Template.Spec.Volumes, v => v.Name == "mssql-system" && v.EmptyDir != null);
        Assert.Contains(sts.Spec.Template.Spec.Volumes, v => v.Name == "mssql-secrets" && v.EmptyDir != null);
        Assert.Contains(sts.Spec.Template.Spec.Volumes, v => v.Name == "mssql-conf" && v.ConfigMap != null);
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
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Contains(container.VolumeMounts, m => m.Name == "tmp" && m.MountPath == "/tmp");
        Assert.Contains(container.VolumeMounts, m => m.Name == "mssql-system" && m.MountPath == "/var/opt/mssql/.system");
        Assert.Contains(container.VolumeMounts, m => m.Name == "machine-key" && m.MountPath == "/var/opt/mssql/.system/machine-key");
        Assert.Contains(sts.Spec.Template.Spec.Volumes, v => v.Name == "mssql-system" && v.EmptyDir != null);
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
        Assert.Contains("ReadWriteMany", dataClaim.Spec.AccessModes);
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
        Assert.NotNull(container.ReadinessProbe.Exec);
        var script = string.Join(" ", container.ReadinessProbe.Exec.Command);
        Assert.Contains("$MSSQL_SA_PASSWORD", script);
        Assert.Contains("SELECT 1", script);

        // Startup probe gives CU upgrade scripts a 15-minute budget before liveness applies.
        Assert.NotNull(container.StartupProbe);
        Assert.True((container.StartupProbe.FailureThreshold * container.StartupProbe.PeriodSeconds) >= 900);

        // Liveness stays TCP with a tolerant threshold (no restart storms under load).
        Assert.NotNull(container.LivenessProbe.TcpSocket);
        Assert.Null(container.LivenessProbe.Exec);
        Assert.True(container.LivenessProbe.FailureThreshold >= 5);
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

        Assert.Equal("kata", sts.Spec.Template.Spec.RuntimeClassName);
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

        Assert.Null(sts.Spec.Template.Spec.RuntimeClassName);
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
        Assert.Contains(container.Env, e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "30570");
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
        Assert.Contains(container.Env, e => e.Name == "MSSQL_MEMORY_LIMIT_MB" && e.Value == "30720");
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

        Assert.Equal(new k8s.Models.ResourceQuantity("4"), container.Resources.Requests["cpu"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("4"), container.Resources.Limits["cpu"]);
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
        Assert.Equal(new k8s.Models.ResourceQuantity("4"), container.Resources.Requests["cpu"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("4"), container.Resources.Limits["cpu"]);

        // Memory: requests == limits (Guaranteed)
        Assert.Equal(new k8s.Models.ResourceQuantity("16Gi"), container.Resources.Requests["memory"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("16Gi"), container.Resources.Limits["memory"]);
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
        Assert.Equal(new k8s.Models.ResourceQuantity("500m"), container.Resources.Requests["cpu"]);
        Assert.Equal(new k8s.Models.ResourceQuantity("2"), container.Resources.Limits["cpu"]);
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

        Assert.Equal("kata", sts.Spec.Template.Spec.RuntimeClassName);
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
        Assert.Equal(2, spread.Count());
        Assert.All(spread, c => Assert.Equal("ScheduleAnyway", c.WhenUnsatisfiable));
        Assert.Equivalent(new[] { "kubernetes.io/hostname", "topology.kubernetes.io/zone" }, spread!.Select(c => c.TopologyKey));
        // Selector matches the AG identity, never version/edition.
        Assert.All(spread, c => Assert.True(c.LabelSelector.MatchLabels.ContainsKey("mssql-operator.evariops.com/ag")));
        Assert.Null(sts.Spec.Template.Spec.Affinity);
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

        Assert.Null(sts.Spec.Template.Spec.TopologySpreadConstraints);
        Assert.NotNull(sts.Spec.Template.Spec.Affinity!.PodAntiAffinity);
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

        Assert.Equal("prod-critical", sts.Spec.Template.Metadata.Labels["mssql-operator.evariops.com/pool"]);
        // The immutable selector must NOT carry the pool label.
        Assert.DoesNotContain("mssql-operator.evariops.com/pool", sts.Spec.Selector.MatchLabels);
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

        Assert.DoesNotContain("mssql-operator.evariops.com/pool", sts.Spec.Template.Metadata.Labels);
        Assert.Null(sts.Spec.Template.Spec.TopologySpreadConstraints);
    }

    [Fact]
    public void VolumeClaimTemplateNames_CoverEveryTemplateTheBuilderProduces()
    {
        // The fencer reconstructs PVC names from this list when the Pod object is gone
        // (an exact match — the previous suffix match detached other instances' volumes). A template
        // added here without updating the list would silently escape fencing.
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "vols", NamespaceProperty = "default", Uid = "uid-v" },
            Spec = new SqlInstanceSpec
            {
                Version = "2022",
                Edition = "Developer",
                Storage = new StorageSpec
                {
                    Data = new StorageVolumeSpec { Size = "10Gi" },
                    Log = new StorageVolumeSpec { Size = "5Gi" },
                    Backup = new StorageVolumeSpec { Size = "10Gi" },
                    SysDb = new StorageVolumeSpec { Size = "1Gi" },
                    TempDb = new StorageVolumeSpec { Size = "1Gi" },
                    ErrorLogs = new StorageVolumeSpec { Size = "1Gi" },
                    CertBackups = new StorageVolumeSpec { Size = "1Gi" },
                }
            }
        };

        var sts = _builder.Build(instance, null);

        var produced = sts.Spec.VolumeClaimTemplates.Select(t => t.Metadata.Name).ToList();
        Assert.Equal(StatefulSetBuilder.VolumeClaimTemplateNames.Length, produced.Count);
        foreach (var name in produced)
            Assert.Contains(name, StatefulSetBuilder.VolumeClaimTemplateNames);
    }

    [Fact]
    public void FencerCandidatePvcNames_AreExactPerTemplate_NoSuffixOvermatch()
    {
        var names = MsSqlOperator.Services.Breakout.ReplicaFencer.CandidatePvcNames("ag-1").ToList();

        Assert.Contains("data-ag-1", names);
        Assert.Contains("log-ag-1", names);
        // The bug this replaces: "data-other-ag-1" ends with "-ag-1" and was matched too.
        Assert.All(names, n => Assert.Matches("^[a-z]+-ag-1$", n));
    }

    [Fact]
    public void PreStopCheckpoint_IsBoundedByAThirdOfTheGracePeriod()
    {
        // -l bounds the login only. Without -t, a long CHECKPOINT consumed the whole
        // grace period BEFORE the SIGTERM and SQL Server got SIGKILLed mid-shutdown — the opposite
        // of what the preStop hook exists for.
        var instance = new SqlInstance
        {
            Metadata = new V1ObjectMeta { Name = "grace", NamespaceProperty = "default", Uid = "uid-g" },
            Spec = new SqlInstanceSpec { Version = "2022", Edition = "Developer" }
        };

        var sts = _builder.Build(instance, null);
        var container = sts.Spec.Template.Spec.Containers[0];
        var preStop = string.Join(" ", container.Lifecycle.PreStop.Exec.Command);

        Assert.Equal(180, sts.Spec.Template.Spec.TerminationGracePeriodSeconds);
        Assert.Contains("-t 60", preStop); // 180 / 3
        Assert.Contains("CHECKPOINT", preStop);
    }
}
