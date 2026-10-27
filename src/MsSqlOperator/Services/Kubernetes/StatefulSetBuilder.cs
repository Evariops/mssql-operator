using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator;

namespace MsSqlOperator.Services.Kubernetes;

public class StatefulSetBuilder(
    IAntiAffinityBuilder antiAffinityBuilder,
    ITopologySpreadBuilder topologySpreadBuilder) : IStatefulSetBuilder
{
    private const string ConfigMapSuffix = "-mssql-conf";
    private const string ConfigHashAnnotation = "mssql-operator.evariops.com/config-hash";

    private static readonly V1PodSecurityContext DefaultPodSecurityContext = new()
    {
        FsGroup = 10001,
        RunAsGroup = 10001,
        RunAsNonRoot = true,
        RunAsUser = 10001,
        SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
        // The default (Always) re-chowns every PVC recursively on every mount. On a
        // multi-TB data volume that adds seconds-to-minutes to each restart — exactly during
        // failover/rolling-update/breakout where restart latency matters most. OnRootMismatch
        // chowns only when the volume root's ownership is wrong (first mount), then never again.
        FsGroupChangePolicy = "OnRootMismatch"
    };

    // The SQL Server image's entrypoint switches users and sqlservr relies on the container
    // runtime's default capability set (SETUID/SETGID/DAC_OVERRIDE, ...) plus the privilege
    // escalation its setcap'd binary needs. Dropping ALL capabilities or forbidding privilege
    // escalation makes sqlservr fail to start ("Operation not permitted"), so the default keeps
    // the runtime defaults and a writable root filesystem. Harden via spec.securityContext when
    // a given image/kernel allows it (e.g. add IPC_LOCK for lock-pages-in-memory, set
    // readOnlyRootFilesystem=true — the operator then mounts the required emptyDir volumes).
    private static readonly V1SecurityContext DefaultSecurityContext = new()
    {
        AllowPrivilegeEscalation = true,
        // Read-only root filesystem by default. The required
        // writable emptyDirs (/tmp, /var/opt/mssql/.system) are mounted automatically, and
        // the errorlog path stays on a mounted volume. Override via spec.securityContext
        // if a custom image needs a writable root.
        ReadOnlyRootFilesystem = true
    };

    public V1StatefulSet Build(SqlInstance instance, string? configHash, string? machineKeySecretName = null)
    {
        var spec = instance.Spec;
        var image = SqlServerImages.GetImage(spec.ImageSelection());
        var labels = BuildLabels(instance.Metadata.Name, spec.Version, spec.Edition);
        // Pool goes on the pod template (and STS metadata) but never the immutable selector, so
        // pool membership stays mutable. Spread/anti-affinity selectors match pod labels.
        var podLabels = WithPool(labels, spec.Pool);
        var identityLabels = new Dictionary<string, string> { [OperatorLabels.Instance] = instance.Metadata.Name };

        var podAnnotations = new Dictionary<string, string>();
        if (configHash != null)
            podAnnotations[ConfigHashAnnotation] = configHash;

        var volumes = BuildVolumes(instance.Metadata.Name, spec.SecurityContext, machineKeySecretName, spec.SaPasswordSecretRef,
            spec.MachineKeySecretRef, spec.Tls);
        var container = BuildSqlContainer(image, spec.Resources, spec.Edition, false, spec.Memory,
            spec.SaPasswordSecretRef, spec.Storage, spec.SecurityContext, machineKeySecretName, spec.RuntimeClassName,
            spec.Probes ?? new ProbesSpec(), (int)(spec.Shutdown?.GracePeriodSeconds ?? 180), spec.Tls);

        return new V1StatefulSet
        {
            Metadata = new V1ObjectMeta
            {
                Name = instance.Metadata.Name,
                NamespaceProperty = instance.Metadata.NamespaceProperty,
                // managed-by goes on the STS metadata only — never the selector/pod template,
                // whose match labels are immutable.
                Labels = WithManagedBy(podLabels),
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
                // The selector is immutable — it carries the identity label only. Version and
                // edition used to be part of it, so a major-version upgrade could never be applied.
                Selector = new V1LabelSelector { MatchLabels = SelectorLabels(identityLabels) },
                // spec.updateStrategy was accepted by the CRD and never wired, so every
                // template change (image, config hash, memory) restarted the instance immediately —
                // whatever the user asked for. OnDelete gives them back control of when SQL Server
                // goes down.
                UpdateStrategy = new V1StatefulSetUpdateStrategy
                {
                    Type = spec.UpdateStrategy?.Type == "OnDelete" ? "OnDelete" : "RollingUpdate"
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = podLabels,
                        Annotations = podAnnotations.Count > 0 ? podAnnotations : null
                    },
                    Spec = new V1PodSpec
                    {
                        RuntimeClassName = spec.RuntimeClassName,
                        // SQL Server needs time to checkpoint; the 30s default
                        // SIGKILLs sqlservr mid-checkpoint and forces a long crash-recovery.
                        TerminationGracePeriodSeconds = spec.Shutdown?.GracePeriodSeconds ?? 180,
                        Containers = [container],
                        Volumes = volumes.Count > 0 ? volumes : null,
                        Affinity = antiAffinityBuilder.Build(spec.AntiAffinity, labels, spec.Pool, spec.TopologySpread?.PoolAntiAffinity),
                        TopologySpreadConstraints = topologySpreadBuilder.Build(spec.TopologySpread, spec.Pool, identityLabels),
                        NodeSelector = WithArchConstraint(spec.NodeSelector),
                        Tolerations = spec.Tolerations,
                        PriorityClassName = spec.PriorityClassName,
                        SecurityContext = MergePodSecurityContext(spec.PodSecurityContext)
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(spec.Storage)
            }
        };
    }

    /// <summary>
    /// A user-supplied podSecurityContext used to REPLACE the operator's default
    /// wholesale, silently dropping <c>fsGroupChangePolicy: OnRootMismatch</c> — the very setting
    /// that stops Kubernetes re-chowning a multi-terabyte data volume on every single mount. The
    /// user's explicit choices win; the fields they did not set keep the operator's safe defaults.
    /// </summary>
    internal static V1PodSecurityContext MergePodSecurityContext(V1PodSecurityContext? user)
    {
        if (user is null)
            return DefaultPodSecurityContext;

        // Merge on a COPY, never on the CR's own object. The `??=` used to write the
        // defaults straight into entity.Spec...podSecurityContext, and any later full Update of the
        // CR in the same pass (failover annotations, rolling-update retry) persisted them — a
        // permanent GitOps drift that ArgoCD reverted and the operator re-applied, forever. The
        // JSON round-trip copies every field the user may have set, including ones this merge does
        // not know about.
        var merged = k8s.KubernetesJson.Deserialize<V1PodSecurityContext>(k8s.KubernetesJson.Serialize(user));
        merged.FsGroup ??= DefaultPodSecurityContext.FsGroup;
        merged.RunAsGroup ??= DefaultPodSecurityContext.RunAsGroup;
        merged.RunAsUser ??= DefaultPodSecurityContext.RunAsUser;
        merged.RunAsNonRoot ??= DefaultPodSecurityContext.RunAsNonRoot;
        merged.SeccompProfile ??= DefaultPodSecurityContext.SeccompProfile;
        merged.FsGroupChangePolicy ??= DefaultPodSecurityContext.FsGroupChangePolicy;
        return merged;
    }

    public V1StatefulSet BuildForAg(SqlAvailabilityGroup ag, string? configHash, string? machineKeySecretName = null)
    {
        var tmpl = ag.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
        var edition = ag.Spec.Edition;
        var image = SqlServerImages.GetImage(tmpl.ImageSelection(edition));
        var labels = new Dictionary<string, string>
        {
            { OperatorLabels.Ag, ag.Metadata.Name },
            { "mssql-operator.evariops.com/version", tmpl.Version },
            { "mssql-operator.evariops.com/edition", edition }
        };
        var podLabels = WithPool(labels, ag.Spec.Pool);
        var identityLabels = new Dictionary<string, string> { [OperatorLabels.Ag] = ag.Metadata.Name };

        // Default: when the user sets neither topologySpread nor antiAffinity,
        // AG replicas get a preferred node+zone self-spread (soft, so mono-zone clusters still schedule).
        var topologySpread = ag.Spec.TopologySpread;
        if (topologySpread == null && ag.Spec.AntiAffinity == null)
            topologySpread = TopologySpreadSpec.DefaultSelfSpread;

        var podAnnotations = new Dictionary<string, string>();
        if (configHash != null)
            podAnnotations[ConfigHashAnnotation] = configHash;

        var volumes = BuildVolumes(ag.Metadata.Name, tmpl.SecurityContext, machineKeySecretName, ag.Spec.SaPasswordSecretRef,
            ag.Spec.MachineKeySecretRef, tmpl.Tls);
        var container = BuildSqlContainer(image, tmpl.Resources, edition, true, tmpl.Memory,
            ag.Spec.SaPasswordSecretRef, tmpl.Storage, tmpl.SecurityContext, machineKeySecretName, tmpl.RuntimeClassName,
            tmpl.Probes ?? new ProbesSpec(), (int)(tmpl.Shutdown?.GracePeriodSeconds ?? 180), tmpl.Tls);

        return new V1StatefulSet
        {
            Metadata = new V1ObjectMeta
            {
                Name = ag.Metadata.Name,
                NamespaceProperty = ag.Metadata.NamespaceProperty,
                // managed-by on the STS metadata only — the selector/pod template labels are immutable.
                Labels = WithManagedBy(podLabels),
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
                // Identity label only — see Build.
                Selector = new V1LabelSelector { MatchLabels = SelectorLabels(identityLabels) },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = podLabels,
                        Annotations = podAnnotations.Count > 0 ? podAnnotations : null
                    },
                    Spec = new V1PodSpec
                    {
                        RuntimeClassName = tmpl.RuntimeClassName,
                        TerminationGracePeriodSeconds = tmpl.Shutdown?.GracePeriodSeconds ?? 180,
                        Containers = [container],
                        Volumes = volumes.Count > 0 ? volumes : null,
                        // Co-located replicas would lose quorum on a single node failure: default to a
                        // preferred node+zone self-spread, overridable via topologySpread/antiAffinity.
                        Affinity = antiAffinityBuilder.Build(ag.Spec.AntiAffinity, labels, ag.Spec.Pool, ag.Spec.TopologySpread?.PoolAntiAffinity),
                        TopologySpreadConstraints = topologySpreadBuilder.Build(topologySpread, ag.Spec.Pool, identityLabels),
                        NodeSelector = WithArchConstraint(tmpl.NodeSelector),
                        Tolerations = tmpl.Tolerations,
                        PriorityClassName = tmpl.PriorityClassName,
                        SecurityContext = MergePodSecurityContext(tmpl.PodSecurityContext)
                    }
                },
                VolumeClaimTemplates = BuildVolumeClaims(tmpl.Storage)
            }
        };
    }

    private static V1Container BuildSqlContainer(string image, ResourceRequirements? resources,
        string edition, bool hadr, MemorySpec? memory, SecretReference? saPasswordSecretRef,
        StorageSpec? storage, V1SecurityContext? securityContext, string? machineKeySecretName,
        string? runtimeClassName, ProbesSpec probes, int terminationGraceSeconds, TlsSpec? tls)
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
            // MSSQL_MEMORY_LIMIT_MB caps the sqlservr PROCESS; sp_configure 'max server
            // memory' (applied by the controller) sits below it, leaving head-room for thread stacks,
            // fdhost and the full-text crawl buffers that live outside the SQL memory budget.
            var processLimitMb = MemoryCalculator.CalculateProcessMemoryLimitMb(memory, runtimeClassName);
            env.Add(new V1EnvVar { Name = "MSSQL_MEMORY_LIMIT_MB", Value = processLimitMb.ToString() });
        }

        var volumeMounts = BuildVolumeMounts(storage, securityContext, machineKeySecretName);
        if (!string.IsNullOrEmpty(tls?.SecretName))
            volumeMounts.Add(new V1VolumeMount { Name = TlsVolume, MountPath = MssqlConfBuilder.TlsMountPath, ReadOnlyProperty = true });
        if (saPasswordSecretRef != null)
            volumeMounts.Add(new V1VolumeMount { Name = SaPasswordVolume, MountPath = SaPasswordMountPath, ReadOnlyProperty = true });

        var container = new V1Container
        {
            Name = "mssql",
            Image = image,
            Ports = [new V1ContainerPort { ContainerPort = 1433, Name = "sql" }],
            Env = env,
            // TCP-only readiness passed before recovery finished, routing traffic to
            // an instance that refused logins. The exec probe requires a successful login +
            // SELECT 1. See SqlcmdShell for how the password reaches sqlcmd.
            //
            // It now also refuses to report Ready while a user database is still in
            // crash recovery. `SELECT 1` on master answers happily minutes before the databases are
            // usable, so the pod was put back in the Service — and the application saw a "successful"
            // restart followed by errors 922/945 on every query. RESTORING is deliberately NOT
            // checked: that is the normal state of a secondary's copy in an availability group.
            //
            // Probe budgets are tolerant by default and configurable (spec.probes) — a
            // 5s timeout with 3 failures pulled a merely-saturated instance out of the Service and
            // turned a slowdown into an outage.
            //
            // The recovery gate is skippable (probes.requireDatabasesRecovered=false).
            // It is all-or-nothing, so a single database stuck in RECOVERY_PENDING for good (lost
            // file) would otherwise keep the pod out of the Service — and out of the operator's
            // reach — for ever, letting one broken database masquerade as a dead replica.
            ReadinessProbe = new V1Probe
            {
                Exec = new V1ExecAction
                {
                    Command =
                    [
                        "/bin/sh", "-c",
                        SqlcmdShell($"-l {probes.ReadinessTimeoutSeconds} -b",
                            probes.RequireDatabasesRecovered
                                ? "\"SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id > 4 AND state_desc IN ('RECOVERING','RECOVERY_PENDING')) " +
                                  "THROW 50001, 'databases are still recovering', 1; SELECT 1\""
                                : "'SELECT 1'")
                    ]
                },
                InitialDelaySeconds = 10,
                PeriodSeconds = 10,
                TimeoutSeconds = probes.ReadinessTimeoutSeconds,
                FailureThreshold = probes.ReadinessFailureThreshold
            },
            // Master upgrade scripts during CU bumps can take many minutes — give the
            // startup phase a 15-minute budget before liveness applies at all.
            StartupProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = 1433 },
                PeriodSeconds = 10,
                FailureThreshold = probes.StartupFailureThreshold
            },
            // An aggressive SQL liveness means restart storms under load; keep TCP
            // with a tolerant threshold.
            LivenessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = 1433 },
                PeriodSeconds = 30,
                FailureThreshold = probes.LivenessFailureThreshold
            },
            // SQL Server handles SIGTERM, but the kubelet sends it and starts counting
            // immediately. A CHECKPOINT first flushes dirty pages, so the shutdown has less to do and
            // the NEXT startup has less crash recovery to replay. Best-effort: the hook must never
            // block a shutdown, hence the `|| true`.
            //
            // -l bounds the LOGIN only; the CHECKPOINT itself needs -t. The preStop hook
            // runs BEFORE the SIGTERM and its duration counts against the grace period, so an
            // unbounded CHECKPOINT ate the whole budget and SQL Server got SIGKILLed mid-shutdown —
            // the exact outcome this hook exists to avoid. A third of the grace period leaves the
            // engine at least two thirds for its own orderly shutdown.
            Lifecycle = new V1Lifecycle
            {
                PreStop = new V1LifecycleHandler
                {
                    Exec = new V1ExecAction
                    {
                        Command =
                        [
                            "/bin/sh", "-c",
                            SqlcmdShell($"-l 5 -t {Math.Clamp(terminationGraceSeconds / 3, 10, 120)}", "'CHECKPOINT'") + " || true"
                        ]
                    }
                }
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

        // The pod ALWAYS
        // writes to ephemeral storage (SMK temp exports, memory dumps when no dedicated volume,
        // sqlcmd probes). Without a request, disk-pressure eviction is unmanaged. The minimal
        // manifest sets neither resources nor memory, so this must apply unconditionally.
        container.Resources ??= new V1ResourceRequirements();
        container.Resources.Requests ??= new Dictionary<string, k8s.Models.ResourceQuantity>();
        if (!container.Resources.Requests.ContainsKey("ephemeral-storage"))
            container.Resources.Requests["ephemeral-storage"] = new k8s.Models.ResourceQuantity("512Mi");

        return container;
    }

    private const string SaPasswordVolume = "sa-password";
    private const string TlsVolume = "mssql-tls";
    internal const string SaPasswordMountPath = "/var/run/secrets/mssql-sa";

    /// <summary>
    /// A shell line running one sqlcmd query as sa (probes, preStop).
    ///
    /// The password goes through SQLCMDPASSWORD, never <c>-P</c> — an argument is visible to
    /// every process of the node in /proc/*/cmdline, an environment variable only to its owner.
    /// It is read from the mounted Secret first. That file follows a rotation; the
    /// MSSQL_SA_PASSWORD variable is frozen at container start, so after the operator converged sa
    /// to the new password the probe failed forever and the instance lost its only Service endpoint.
    /// The variable stays as the fallback for the window before the operator runs ALTER LOGIN.
    /// </summary>
    internal static string SqlcmdShell(string options, string query)
        => "SQLCMD=$(command -v /opt/mssql-tools18/bin/sqlcmd || command -v /opt/mssql-tools/bin/sqlcmd) || exit 1; " +
           $"run() {{ SQLCMDPASSWORD=\"$1\" \"$SQLCMD\" -S localhost -U sa {options} -C -Q {query}; }}; " +
           $"run \"$(cat {SaPasswordMountPath}/password 2>/dev/null)\" || run \"$MSSQL_SA_PASSWORD\"";

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

        // /tmp must be writable; only needed as an emptyDir when the root filesystem is read-only.
        var readOnly = securityContext?.ReadOnlyRootFilesystem
                       ?? DefaultSecurityContext.ReadOnlyRootFilesystem
                       ?? false;
        if (readOnly)
            mounts.Add(new V1VolumeMount { Name = "tmp", MountPath = "/tmp" });

        // sqlservr creates /var/opt/mssql/secrets on startup and aborts if it cannot ("The secrets
        // directory [...] could not be created. Read-only file system"). It is a writable,
        // fsGroup-owned emptyDir whenever the root filesystem is read-only OR the machine key is
        // mounted into it: without the emptyDir the kubelet creates the directory as root for the
        // subPath mount, and SQL Server (uid 10001) cannot write there.
        if (readOnly || machineKeySecretName != null)
            mounts.Add(new V1VolumeMount { Name = "mssql-secrets", MountPath = "/var/opt/mssql/secrets" });

        // /var/opt/mssql/.system must be writable under a read-only root (.system/system).
        if (readOnly)
            mounts.Add(new V1VolumeMount { Name = "mssql-system", MountPath = "/var/opt/mssql/.system" });

        // Machine key secret mount: a file inside the secrets emptyDir above, where the engine reads
        // it.
        if (machineKeySecretName != null)
            mounts.Add(MachineKeyManager.BuildMachineKeyMount());

        return mounts;
    }

    private static List<V1Volume> BuildVolumes(string name, V1SecurityContext? securityContext, string? machineKeySecretName,
        SecretReference? saPasswordSecretRef, SecretReference? machineKeySecretRef, TlsSpec? tls)
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
            volumes.Add(new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() });

        // Writable /var/opt/mssql/secrets for sqlservr startup and the machine key (see BuildVolumeMounts).
        if (readOnly || machineKeySecretName != null)
            volumes.Add(new V1Volume { Name = "mssql-secrets", EmptyDir = new V1EmptyDirVolumeSource() });

        // See BuildVolumeMounts: .system needs a writable emptyDir under a read-only root.
        if (readOnly)
            volumes.Add(new V1Volume { Name = "mssql-system", EmptyDir = new V1EmptyDirVolumeSource() });

        // Machine key secret volume
        if (machineKeySecretName != null)
        {
            volumes.Add(MachineKeyManager.BuildMachineKeyVolume(machineKeySecretName,
                MachineKeyManager.SecretKeyFor(machineKeySecretName, machineKeySecretRef)));
        }

        // The sa password as a file the kubelet refreshes in place (no subPath), read by the
        // probes — the environment variable is frozen at container start.
        if (saPasswordSecretRef != null)
        {
            volumes.Add(new V1Volume
            {
                Name = SaPasswordVolume,
                Secret = new V1SecretVolumeSource
                {
                    SecretName = saPasswordSecretRef.Name,
                    Items = [new V1KeyToPath { Key = saPasswordSecretRef.Key, Path = "password" }],
                    DefaultMode = 288, // 0440: readable through the fsGroup
                },
            });
        }

        // The certificate SQL Server presents (mssql.conf network.tlscert / tlskey).
        if (!string.IsNullOrEmpty(tls?.SecretName))
        {
            volumes.Add(new V1Volume
            {
                Name = TlsVolume,
                Secret = new V1SecretVolumeSource
                {
                    SecretName = tls.SecretName,
                    Items = [new V1KeyToPath { Key = "tls.crt", Path = "tls.crt" }, new V1KeyToPath { Key = "tls.key", Path = "tls.key" }],
                    DefaultMode = 288, // 0440: readable through the fsGroup
                },
            });
        }

        return volumes;
    }

    private static Dictionary<string, k8s.Models.ResourceQuantity>? BuildResourceDict(Entities.Common.ResourceQuantity? rq)
    {
        if (rq == null) return null;
        var dict = new Dictionary<string, k8s.Models.ResourceQuantity>();
        if (rq.Cpu != null) dict["cpu"] = new k8s.Models.ResourceQuantity(rq.Cpu);
        if (rq.Memory != null) dict["memory"] = new k8s.Models.ResourceQuantity(rq.Memory);
        if (rq.EphemeralStorage != null) dict["ephemeral-storage"] = new k8s.Models.ResourceQuantity(rq.EphemeralStorage);
        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// Every volumeClaimTemplate name this builder can produce. The fencer uses it to
    /// reconstruct the EXACT PVC names of a pod whose object is already gone — a suffix match
    /// (<c>EndsWith("-{pod}")</c>) also matched the PVCs of another StatefulSet whose pod names end
    /// identically ("data-other-ag-1" for pod "ag-1"), and fencing then detached an innocent
    /// instance's volumes. Keep in sync with <see cref="BuildVolumeClaims"/> (a test asserts it).
    /// </summary>
    internal static readonly string[] VolumeClaimTemplateNames =
        ["data", "log", "backup", "sysdb", "tempdb", "errorlogs", "certbackups"];

    /// <summary>The volumes provisioned when the spec leaves them out (also what admission compares
    /// an omitted volume against).</summary>
    internal static StorageVolumeSpec DefaultDataVolume => new() { Size = "10Gi" };
    internal static StorageVolumeSpec DefaultLogVolume => new() { Size = "5Gi" };
    internal static StorageVolumeSpec DefaultBackupVolume => new() { Size = "10Gi" };

    private static List<V1PersistentVolumeClaim> BuildVolumeClaims(StorageSpec? storage)
    {
        var claims = new List<V1PersistentVolumeClaim>();

        // Required volumes (always provisioned)
        AddClaim(claims, "data", storage?.Data ?? DefaultDataVolume);
        AddClaim(claims, "log", storage?.Log ?? DefaultLogVolume);
        AddClaim(claims, "backup", storage?.Backup ?? DefaultBackupVolume);
        if (storage == null)
            return claims;

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

    /// <summary>A copy of the identity labels for the StatefulSet selector (never shared with the
    /// spread constraints built from the same map).</summary>
    private static Dictionary<string, string> SelectorLabels(Dictionary<string, string> identityLabels) => new(identityLabels);

    private static Dictionary<string, string> BuildLabels(string name, string version, string edition) => new()
    {
        { "mssql-operator.evariops.com/instance", name },
        { "mssql-operator.evariops.com/version", version },
        { "mssql-operator.evariops.com/edition", edition }
    };

    // A copy of the selector labels plus the common managed-by scoping label, for object metadata
    // Returning a copy keeps the immutable selector/pod-template labels untouched.
    private static Dictionary<string, string> WithManagedBy(Dictionary<string, string> labels) =>
        new(labels) { [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue };

    // Pool label for cross-resource grouping. Returns the same dictionary unchanged when no
    // pool is set, so the selector and pod template keep sharing one reference (current behaviour).
    private static Dictionary<string, string> WithPool(Dictionary<string, string> labels, string? pool) =>
        string.IsNullOrEmpty(pool) ? labels : new(labels) { [OperatorLabels.Pool] = pool };

    private const string ArchNodeSelectorKey = "kubernetes.io/arch";

    /// <summary>
    /// SQL Server container images are amd64-only. Pin the pod to amd64 nodes so a mixed-arch
    /// cluster never schedules it onto arm64 — that fails with "exec format error" in an endless
    /// CrashLoop with no actionable message. A user who set kubernetes.io/arch explicitly wins
    /// (their override is respected). Returns a fresh dictionary so no caller state is mutated.
    /// </summary>
    private static IDictionary<string, string> WithArchConstraint(IDictionary<string, string>? userSelector)
    {
        var result = userSelector is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(userSelector);
        if (!result.ContainsKey(ArchNodeSelectorKey))
            result[ArchNodeSelectorKey] = "amd64";
        return result;
    }
}
