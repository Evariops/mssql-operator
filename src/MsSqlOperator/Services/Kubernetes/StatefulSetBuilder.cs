using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Kubernetes;

public class StatefulSetBuilder(IAntiAffinityBuilder antiAffinityBuilder) : IStatefulSetBuilder
{
    private const string ConfigMapSuffix = "-mssql-conf";
    private const string ConfigHashAnnotation = "mssql.operator/config-hash";

    private static readonly V1PodSecurityContext DefaultPodSecurityContext = new()
    {
        FsGroup = 10001,
        RunAsGroup = 10001,
        RunAsNonRoot = true,
        RunAsUser = 10001,
        SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
    };

    private static readonly V1SecurityContext DefaultSecurityContext = new()
    {
        AllowPrivilegeEscalation = false,
        Capabilities = new V1Capabilities
        {
            Add = ["IPC_LOCK"],
            Drop = ["ALL"]
        },
        ReadOnlyRootFilesystem = true
    };

    public V1StatefulSet Build(SqlInstance instance, string? configHash, string? machineKeySecretName = null)
    {
        var spec = instance.Spec;
        var image = SqlServerImages.GetImage(spec.Version, spec.Edition, spec.Image, spec.ImageTag);
        var labels = BuildLabels(instance.Metadata.Name, spec.Version, spec.Edition);

        var podAnnotations = new Dictionary<string, string>();
        if (configHash != null)
            podAnnotations[ConfigHashAnnotation] = configHash;

        var volumes = BuildVolumes(instance.Metadata.Name, spec.SecurityContext, machineKeySecretName);
        var container = BuildSqlContainer(image, spec.Resources, spec.Edition, false, spec.Memory,
            spec.SaPasswordSecretRef, spec.Storage, spec.SecurityContext, machineKeySecretName, spec.RuntimeClassName);

        return new V1StatefulSet
        {
            Metadata = new V1ObjectMeta
            {
                Name = instance.Metadata.Name,
                NamespaceProperty = instance.Metadata.NamespaceProperty,
                Labels = labels,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = instance.ApiVersion, Kind = instance.Kind,
                        Name = instance.Metadata.Name, Uid = instance.Metadata.Uid,
                        Controller = true, BlockOwnerDeletion = true
                    }
                ]
            },
            Spec = new V1StatefulSetSpec
            {
                Replicas = 1,
                ServiceName = instance.Metadata.Name,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = podAnnotations.Count > 0 ? podAnnotations : null
                    },
                    Spec = new V1PodSpec
                    {
                        RuntimeClassName = spec.RuntimeClassName,
                        Containers = [container],
                        Volumes = volumes.Count > 0 ? volumes : null,
                        Affinity = antiAffinityBuilder.Build(spec.AntiAffinity, labels),
                        NodeSelector = spec.NodeSelector,
                        Tolerations = spec.Tolerations,
                        PriorityClassName = spec.PriorityClassName,
                        SecurityContext = spec.PodSecurityContext ?? DefaultPodSecurityContext
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(spec.Storage)
            }
        };
    }

    public V1StatefulSet BuildForAg(SqlAvailabilityGroup ag, string? configHash, string? machineKeySecretName = null)
    {
        var tmpl = ag.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        var edition = ag.Spec.Edition;
        var image = SqlServerImages.GetImage(tmpl.Version, edition, tmpl.Image, tmpl.ImageTag);
        var labels = new Dictionary<string, string>
        {
            { "mssql.operator/ag", ag.Metadata.Name },
            { "mssql.operator/version", tmpl.Version },
            { "mssql.operator/edition", edition }
        };

        var podAnnotations = new Dictionary<string, string>();
        if (configHash != null)
            podAnnotations[ConfigHashAnnotation] = configHash;

        var volumes = BuildVolumes(ag.Metadata.Name, tmpl.SecurityContext, machineKeySecretName);
        var container = BuildSqlContainer(image, tmpl.Resources, edition, true, tmpl.Memory,
            ag.Spec.SaPasswordSecretRef, tmpl.Storage, tmpl.SecurityContext, machineKeySecretName, tmpl.RuntimeClassName);

        return new V1StatefulSet
        {
            Metadata = new V1ObjectMeta
            {
                Name = ag.Metadata.Name,
                NamespaceProperty = ag.Metadata.NamespaceProperty,
                Labels = labels,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = ag.ApiVersion, Kind = ag.Kind,
                        Name = ag.Metadata.Name, Uid = ag.Metadata.Uid,
                        Controller = true, BlockOwnerDeletion = true
                    }
                ]
            },
            Spec = new V1StatefulSetSpec
            {
                Replicas = ag.Spec.Replicas.Count,
                ServiceName = $"{ag.Metadata.Name}-headless",
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = podAnnotations.Count > 0 ? podAnnotations : null
                    },
                    Spec = new V1PodSpec
                    {
                        RuntimeClassName = tmpl.RuntimeClassName,
                        Containers = [container],
                        Volumes = volumes.Count > 0 ? volumes : null,
                        // AG replicas default to preferred hostname spread: co-located replicas
                        // would lose quorum on a single node failure.
                        Affinity = antiAffinityBuilder.Build(ag.Spec.AntiAffinity ?? AntiAffinitySpec.PreferredHostnameSpread, labels),
                        NodeSelector = tmpl.NodeSelector,
                        Tolerations = tmpl.Tolerations,
                        PriorityClassName = tmpl.PriorityClassName,
                        SecurityContext = tmpl.PodSecurityContext ?? DefaultPodSecurityContext
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(tmpl.Storage)
            }
        };
    }

    private static V1Container BuildSqlContainer(string image, ResourceRequirements? resources,
        string edition, bool hadr, MemorySpec? memory, SecretReference? saPasswordSecretRef,
        StorageSpec? storage, V1SecurityContext? securityContext, string? machineKeySecretName,
        string? runtimeClassName)
    {
        var env = new List<V1EnvVar>
        {
            new() { Name = "ACCEPT_EULA", Value = "Y" },
            new() { Name = "MSSQL_PID", Value = edition },
            new() { Name = "MSSQL_AGENT_ENABLED", Value = "true" }
        };

        // SA password via secretKeyRef
        if (saPasswordSecretRef != null)
        {
            env.Add(new V1EnvVar
            {
                Name = "MSSQL_SA_PASSWORD",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = saPasswordSecretRef.Name,
                        Key = saPasswordSecretRef.Key
                    }
                }
            });
        }

        if (hadr)
        {
            env.Add(new V1EnvVar { Name = "MSSQL_ENABLE_HADR", Value = "1" });
        }

        if (memory != null)
        {
            var sqlMaxMemoryMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memory, runtimeClassName);
            env.Add(new V1EnvVar { Name = "MSSQL_MEMORY_LIMIT_MB", Value = sqlMaxMemoryMb.ToString() });
        }

        var volumeMounts = BuildVolumeMounts(storage, securityContext, machineKeySecretName);

        var container = new V1Container
        {
            Name = "mssql",
            Image = image,
            Ports = [new V1ContainerPort { ContainerPort = 1433, Name = "sql" }],
            Env = env,
            ReadinessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = 1433 },
                InitialDelaySeconds = 15,
                PeriodSeconds = 10
            },
            LivenessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = 1433 },
                InitialDelaySeconds = 30,
                PeriodSeconds = 30
            },
            VolumeMounts = volumeMounts,
            SecurityContext = securityContext ?? DefaultSecurityContext
        };

        if (resources != null || memory != null)
        {
            container.Resources = new V1ResourceRequirements
            {
                Requests = BuildResourceDict(resources?.Requests),
                Limits = BuildResourceDict(resources?.Limits)
            };

            // CpuPinning: equalize CPU requests and limits for Guaranteed QoS / static CPU policy
            if (resources?.CpuPinning == true && resources.Limits?.Cpu != null)
            {
                container.Resources.Requests ??= new Dictionary<string, k8s.Models.ResourceQuantity>();
                container.Resources.Limits ??= new Dictionary<string, k8s.Models.ResourceQuantity>();
                var cpuQuantity = new k8s.Models.ResourceQuantity(resources.Limits.Cpu);
                container.Resources.Requests["cpu"] = cpuQuantity;
                container.Resources.Limits["cpu"] = cpuQuantity;
            }

            if (memory != null)
            {
                var memoryQuantity = new k8s.Models.ResourceQuantity(memory.Total);
                container.Resources.Requests ??= new Dictionary<string, k8s.Models.ResourceQuantity>();
                container.Resources.Limits ??= new Dictionary<string, k8s.Models.ResourceQuantity>();
                container.Resources.Requests["memory"] = memoryQuantity;
                container.Resources.Limits["memory"] = memoryQuantity;
            }
        }

        return container;
    }

    private static List<V1VolumeMount> BuildVolumeMounts(StorageSpec? storage, V1SecurityContext? securityContext, string? machineKeySecretName)
    {
        var mounts = new List<V1VolumeMount>();

        // PVC-backed mounts — always mount data/log/backup, others if defined
        mounts.Add(new V1VolumeMount { Name = "data", MountPath = "/var/opt/mssql/data" });
        mounts.Add(new V1VolumeMount { Name = "log", MountPath = "/var/opt/mssql/log" });
        mounts.Add(new V1VolumeMount { Name = "backup", MountPath = "/var/opt/mssql/backup" });

        if (storage?.SysDb != null)
            mounts.Add(new V1VolumeMount { Name = "sysdb", MountPath = "/var/opt/mssql/sysdb" });
        if (storage?.TempDb != null)
            mounts.Add(new V1VolumeMount { Name = "tempdb", MountPath = "/var/opt/mssql/tempdb" });
        if (storage?.ErrorLogs != null)
            mounts.Add(new V1VolumeMount { Name = "errorlogs", MountPath = "/var/opt/mssql/errorlogs" });
        if (storage?.CertBackups != null)
            mounts.Add(new V1VolumeMount { Name = "certbackups", MountPath = "/var/opt/mssql/tde-backups" });

        // ConfigMap mount for mssql.conf
        mounts.Add(new V1VolumeMount
        {
            Name = "mssql-conf",
            MountPath = "/var/opt/mssql/mssql.conf",
            SubPath = "mssql.conf",
            ReadOnlyProperty = true
        });

        // emptyDir mounts for readOnlyRootFilesystem
        var readOnly = securityContext?.ReadOnlyRootFilesystem
                       ?? DefaultSecurityContext.ReadOnlyRootFilesystem
                       ?? false;
        if (readOnly)
        {
            mounts.Add(new V1VolumeMount { Name = "tmp", MountPath = "/tmp" });
            mounts.Add(new V1VolumeMount { Name = "mssql-system", MountPath = "/var/opt/mssql/.system" });
        }

        // Machine key secret mount (overrides the machine-key file inside .system emptyDir)
        if (machineKeySecretName != null)
        {
            mounts.Add(MachineKeyManager.BuildMachineKeyMount());
        }

        return mounts;
    }

    private static List<V1Volume> BuildVolumes(string name, V1SecurityContext? securityContext, string? machineKeySecretName)
    {
        var volumes = new List<V1Volume>
        {
            // ConfigMap volume for mssql.conf
            new()
            {
                Name = "mssql-conf",
                ConfigMap = new V1ConfigMapVolumeSource
                {
                    Name = $"{name}{ConfigMapSuffix}"
                }
            }
        };

        var readOnly = securityContext?.ReadOnlyRootFilesystem
                       ?? DefaultSecurityContext.ReadOnlyRootFilesystem
                       ?? false;
        if (readOnly)
        {
            volumes.Add(new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() });
            volumes.Add(new V1Volume { Name = "mssql-system", EmptyDir = new V1EmptyDirVolumeSource() });
        }

        // Machine key secret volume
        if (machineKeySecretName != null)
        {
            volumes.Add(MachineKeyManager.BuildMachineKeyVolume(machineKeySecretName));
        }

        return volumes;
    }

    private static Dictionary<string, k8s.Models.ResourceQuantity>? BuildResourceDict(Entities.Common.ResourceQuantity? rq)
    {
        if (rq == null) return null;
        var dict = new Dictionary<string, k8s.Models.ResourceQuantity>();
        if (rq.Cpu != null) dict["cpu"] = new k8s.Models.ResourceQuantity(rq.Cpu);
        if (rq.Memory != null) dict["memory"] = new k8s.Models.ResourceQuantity(rq.Memory);
        return dict.Count > 0 ? dict : null;
    }

    private static List<V1PersistentVolumeClaim> BuildVolumeClaims(StorageSpec? storage)
    {
        var claims = new List<V1PersistentVolumeClaim>();
        if (storage == null)
        {
            storage = new StorageSpec
            {
                Data = new StorageVolumeSpec { Size = "10Gi" },
                Log = new StorageVolumeSpec { Size = "5Gi" },
                Backup = new StorageVolumeSpec { Size = "10Gi" }
            };
        }

        // Required volumes (always provisioned)
        AddClaim(claims, "data", storage.Data ?? new StorageVolumeSpec { Size = "10Gi" });
        AddClaim(claims, "log", storage.Log ?? new StorageVolumeSpec { Size = "5Gi" });
        AddClaim(claims, "backup", storage.Backup ?? new StorageVolumeSpec { Size = "10Gi" });

        // Optional volumes (provisioned only when defined in spec)
        if (storage.SysDb != null) AddClaim(claims, "sysdb", storage.SysDb);
        if (storage.TempDb != null) AddClaim(claims, "tempdb", storage.TempDb);
        if (storage.ErrorLogs != null) AddClaim(claims, "errorlogs", storage.ErrorLogs);
        if (storage.CertBackups != null) AddClaim(claims, "certbackups", storage.CertBackups);

        return claims;
    }

    private static void AddClaim(List<V1PersistentVolumeClaim> claims, string name, StorageVolumeSpec vol)
    {
        var claim = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = vol.AccessModes ?? ["ReadWriteOnce"],
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, k8s.Models.ResourceQuantity>
                    {
                        { "storage", new k8s.Models.ResourceQuantity(vol.Size) }
                    }
                }
            }
        };
        if (vol.StorageClassName != null) claim.Spec.StorageClassName = vol.StorageClassName;
        claims.Add(claim);
    }

    private static Dictionary<string, string> BuildLabels(string name, string version, string edition) => new()
    {
        { "mssql.operator/instance", name },
        { "mssql.operator/version", version },
        { "mssql.operator/edition", edition }
    };
}
