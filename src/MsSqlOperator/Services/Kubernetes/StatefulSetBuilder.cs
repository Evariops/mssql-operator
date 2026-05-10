using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Kubernetes;

public class StatefulSetBuilder(IAntiAffinityBuilder antiAffinityBuilder) : IStatefulSetBuilder
{
    public V1StatefulSet Build(SqlInstance instance, string saPassword)
    {
        var spec = instance.Spec;
        var image = SqlServerImages.GetImage(spec.Version, spec.Edition, spec.Image, spec.ImageTag);
        var labels = BuildLabels(instance.Metadata.Name, spec.Version, spec.Edition);

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
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            BuildSqlContainer(image, spec.Resources, saPassword, spec.Edition, false)
                        ],
                        Affinity = antiAffinityBuilder.Build(spec.AntiAffinity, labels)
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(spec.Storage)
            }
        };
    }

    public V1StatefulSet BuildForAg(SqlAvailabilityGroup ag, string saPassword)
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
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            BuildSqlContainer(image, tmpl.Resources, saPassword, edition, true)
                        ],
                        Affinity = antiAffinityBuilder.Build(ag.Spec.AntiAffinity, labels)
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(tmpl.Storage)
            }
        };
    }

    private static V1Container BuildSqlContainer(string image, Entities.Common.ResourceRequirements? resources, string saPassword, string edition, bool hadr)
    {
        var env = new List<V1EnvVar>
        {
            new() { Name = "ACCEPT_EULA", Value = "Y" },
            new() { Name = "MSSQL_PID", Value = edition },
            new() { Name = "MSSQL_SA_PASSWORD", Value = saPassword },
            new() { Name = "MSSQL_AGENT_ENABLED", Value = "true" }
        };

        if (hadr)
        {
            env.Add(new V1EnvVar { Name = "MSSQL_ENABLE_HADR", Value = "1" });
        }

        var container = new V1Container
        {
            Name = "mssql",
            Image = image,
            Ports = [new V1ContainerPort { ContainerPort = 1433, Name = "sql" }],
            Env = env,
            ReadinessProbe = new V1Probe
            {
                Exec = new V1ExecAction { Command = ["/opt/mssql-tools/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", saPassword, "-Q", "SELECT 1"] },
                InitialDelaySeconds = 15,
                PeriodSeconds = 10
            },
            LivenessProbe = new V1Probe
            {
                Exec = new V1ExecAction { Command = ["/opt/mssql-tools/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", saPassword, "-Q", "SELECT 1"] },
                InitialDelaySeconds = 30,
                PeriodSeconds = 30
            },
            VolumeMounts =
            [
                new V1VolumeMount { Name = "data", MountPath = "/var/opt/mssql/data" },
                new V1VolumeMount { Name = "log", MountPath = "/var/opt/mssql/log" },
                new V1VolumeMount { Name = "backup", MountPath = "/var/opt/mssql/backup" }
            ]
        };

        if (resources != null)
        {
            container.Resources = new V1ResourceRequirements
            {
                Requests = BuildResourceDict(resources.Requests),
                Limits = BuildResourceDict(resources.Limits)
            };
        }

        return container;
    }

    private static Dictionary<string, ResourceQuantity>? BuildResourceDict(Entities.Common.ResourceQuantity? rq)
    {
        if (rq == null) return null;
        var dict = new Dictionary<string, ResourceQuantity>();
        if (rq.Cpu != null) dict["cpu"] = new ResourceQuantity(rq.Cpu);
        if (rq.Memory != null) dict["memory"] = new ResourceQuantity(rq.Memory);
        return dict.Count > 0 ? dict : null;
    }

    private static List<V1PersistentVolumeClaim> BuildVolumeClaims(Entities.Common.StorageSpec? storage)
    {
        var claims = new List<V1PersistentVolumeClaim>();
        if (storage == null)
        {
            storage = new Entities.Common.StorageSpec
            {
                Data = new Entities.Common.StorageVolumeSpec { Size = "10Gi" },
                Log = new Entities.Common.StorageVolumeSpec { Size = "5Gi" },
                Backup = new Entities.Common.StorageVolumeSpec { Size = "10Gi" }
            };
        }

        AddClaim(claims, "data", storage.Data ?? new Entities.Common.StorageVolumeSpec { Size = "10Gi" });
        AddClaim(claims, "log", storage.Log ?? new Entities.Common.StorageVolumeSpec { Size = "5Gi" });
        AddClaim(claims, "backup", storage.Backup ?? new Entities.Common.StorageVolumeSpec { Size = "10Gi" });

        return claims;
    }

    private static void AddClaim(List<V1PersistentVolumeClaim> claims, string name, Entities.Common.StorageVolumeSpec vol)
    {
        var claim = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        { "storage", new ResourceQuantity(vol.Size) }
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
