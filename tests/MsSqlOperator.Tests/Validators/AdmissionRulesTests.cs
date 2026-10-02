using k8s.Models;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

/// <summary>Admission and schema rules.
/// </summary>
public class AdmissionRulesTests
{
    private static SqlInstance Instance(Action<SqlInstanceSpec>? configure = null)
    {
        var spec = new SqlInstanceSpec
        {
            Mode = "SingleNode",
            Version = "2022",
            Edition = "Developer",
            Memory = new MemorySpec { Total = "8Gi" },
            SaPasswordSecretRef = new SecretReference { Name = "sa", Key = "password" },
        };
        configure?.Invoke(spec);
        return new SqlInstance { Metadata = new V1ObjectMeta { Name = "x", NamespaceProperty = "ns" }, Spec = spec };
    }

    private static SqlAvailabilityGroup Ag(Action<SqlAvailabilityGroupSpec>? configure = null)
    {
        var spec = new SqlAvailabilityGroupSpec
        {
            Edition = "Enterprise",
            Replicas = new AgReplicaTopologySpec { Count = 3, SynchronousCount = 2 },
            SaPasswordSecretRef = new SecretReference { Name = "sa", Key = "password" },
            MasterKeyPasswordSecretRef = new SecretReference { Name = "mk", Key = "password" },
            HadrCertificate = new AgHadrCertificateSpec { Source = "Generate" },
            InstanceTemplate = new AgInstanceTemplateSpec { Version = "2022" },
        };
        configure?.Invoke(spec);
        return new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "ns" }, Spec = spec };
    }

    // ── ────────────────────────────────────────────────

    [Fact]
    public void Storage_AddingAnOptionalVolume_IsRefused()
    {
        var error = StorageMutationValidator.Validate(new StorageSpec(), new StorageSpec { SysDb = new StorageVolumeSpec() }, "spec.storage");
        Assert.Contains("sysDb cannot be added", error);
    }

    [Fact]
    public void Storage_RemovingAnOptionalVolume_IsRefused()
        => Assert.Contains("errorLogs cannot be removed",
            StorageMutationValidator.Validate(new StorageSpec { ErrorLogs = new StorageVolumeSpec() }, null, "spec.storage"));

    [Fact]
    public void Storage_SpellingOutADefaultVolume_OrGrowingIt_IsAllowed()
    {
        Assert.Null(StorageMutationValidator.Validate(null, new StorageSpec { Data = new StorageVolumeSpec { Size = "10Gi" } }, "s"));
        Assert.Null(StorageMutationValidator.Validate(null, new StorageSpec { Log = new StorageVolumeSpec { Size = "50Gi" } }, "s"));
    }

    [Theory]
    [InlineData(null, "fast", "1Gi", "10Gi", "storageClassName is immutable")]
    [InlineData(null, null, "20Gi", "10Gi", "cannot shrink")]
    public void Storage_ClassOrShrink_OnAnImplicitVolume_IsRefused(string? oldClass, string? newClass, string oldSize, string newSize, string expected)
    {
        var old = new StorageSpec { Data = new StorageVolumeSpec { Size = oldSize, StorageClassName = oldClass } };
        var updated = new StorageSpec { Data = new StorageVolumeSpec { Size = newSize, StorageClassName = newClass } };
        Assert.Contains(expected, StorageMutationValidator.Validate(old, updated, "s"));
    }

    [Fact]
    public void Storage_AccessModesChange_IsRefused()
        => Assert.Contains("accessModes is immutable", StorageMutationValidator.Validate(
            null, new StorageSpec { Backup = new StorageVolumeSpec { AccessModes = ["ReadWriteMany"] } }, "s"));

    [Fact]
    public void AgUpdate_AddingAVolume_IsRefused()
    {
        var old = Ag();
        var updated = Ag(s => s.InstanceTemplate!.Storage = new StorageSpec { TempDb = new StorageVolumeSpec() });
        var result = new SqlAvailabilityGroupValidator().Update(old, updated, false);
        Assert.False(result.Valid);
        Assert.Contains("instanceTemplate.storage.tempDb", result.Message);
    }

    // ── ────────────────────────────────────────────────

    [Fact]
    public void Ag_UnimplementedFailoverLevel_IsRefusedOnCreate()
    {
        var result = new SqlAvailabilityGroupValidator().Create(Ag(s => s.Failover = new AgFailoverSpec { FailoverConditionLevel = 3 }), false);
        Assert.False(result.Valid);
        Assert.Contains("failover.failoverConditionLevel", result.Message);
    }

    [Fact]
    public void Ag_StoredUnimplementedValue_OnlyWarnsWhenUnchanged()
    {
        // Objects created before the refusal carry the former CRD default (3): an unrelated update passes.
        var old = Ag(s => s.Failover = new AgFailoverSpec { FailoverConditionLevel = 3 });
        var updated = Ag(s => { s.Failover = new AgFailoverSpec { FailoverConditionLevel = 3 }; s.Listener = new AgListenerSpec { Port = 1433 }; });
        var result = new SqlAvailabilityGroupValidator().Update(old, updated, false);
        Assert.True(result.Valid);
        Assert.Contains(result.Warnings!, w => w.StartsWith("failover.failoverConditionLevel", StringComparison.Ordinal));
    }

    [Fact]
    public void Ag_ChangingToAnUnimplementedValue_IsRefused()
    {
        var old = Ag();
        var updated = Ag(s => s.Failover = new AgFailoverSpec { AutoResumeDataMovement = false });
        Assert.False(new SqlAvailabilityGroupValidator().Update(old, updated, false).Valid);
    }

    [Theory]
    [InlineData("maxUnavailable")]
    [InlineData("updateSecondariesFirst")]
    [InlineData("preUpdateBackup")]
    public void Ag_UnimplementedRollingUpdateKnobs_AreRefused(string knob)
    {
        var rolling = new RollingUpdateSpec();
        switch (knob)
        {
            case "maxUnavailable": rolling.MaxUnavailable = 2; break;
            case "updateSecondariesFirst": rolling.UpdateSecondariesFirst = false; break;
            default: rolling.PreUpdateBackup = true; break;
        }

        var result = new SqlAvailabilityGroupValidator().Create(Ag(s => s.UpdateStrategy = new UpdateStrategySpec { RollingUpdate = rolling }), false);
        Assert.False(result.Valid);
        Assert.Contains(knob, result.Message);
    }

    [Fact]
    public void Ag_DefaultRollingUpdate_IsAccepted()
        => Assert.True(new SqlAvailabilityGroupValidator()
            .Create(Ag(s => s.UpdateStrategy = new UpdateStrategySpec { RollingUpdate = new RollingUpdateSpec() }), false).Valid);

    [Fact]
    public void Ag_OperatorLoginAndHadrKeyLength_AreRefused()
    {
        var validator = new SqlAvailabilityGroupValidator();
        Assert.False(validator.Create(Ag(s => s.OperatorLoginSecretRef = new SqlLoginSecretReference()), false).Valid);
        Assert.False(validator.Create(Ag(s => s.HadrCertificate!.Generate = new CertificateGenerateSpec { KeyLength = 4096 }), false).Valid);
    }

    [Fact]
    public void Ag_HadrExpiryDate_IsValidated()
    {
        var validator = new SqlAvailabilityGroupValidator();
        Assert.True(validator.Create(Ag(s => s.HadrCertificate!.Generate = new CertificateGenerateSpec { ExpiryDate = "2031-01-01" }), false).Valid);
        Assert.False(validator.Create(Ag(s => s.HadrCertificate!.Generate = new CertificateGenerateSpec { ExpiryDate = "soon" }), false).Valid);
    }

    [Fact]
    public void Instance_AvailabilityGroupMemberMode_IsRefused()
    {
        var result = new SqlInstanceValidator().Create(Instance(s => s.Mode = "AvailabilityGroupMember"), false);
        Assert.False(result.Valid);
        Assert.Contains("SqlAvailabilityGroup", result.Message);
    }

    [Fact]
    public void Instance_RollingUpdateBlock_IsRefused()
        => Assert.False(new SqlInstanceValidator().Create(
            Instance(s => s.UpdateStrategy = new UpdateStrategySpec { RollingUpdate = new RollingUpdateSpec() }), false).Valid);

    [Fact]
    public void Configuration_UnappliedServerSettings_AreRefused()
    {
        var validator = new SqlConfigurationValidator();
        var config = new SqlConfiguration { Spec = new SqlConfigurationSpec { ServerSettings = new ServerSettingsSpec { NumErrorLogs = 10 } } };
        Assert.False(validator.Create(config, false).Valid);
        config.Spec.ServerSettings = new ServerSettingsSpec { AuthenticationMode = "Windows" };
        Assert.False(validator.Create(config, false).Valid);
    }

    [Fact]
    public void Tde_UnimplementedRetirementAndKeyLength_AreRefused()
    {
        static SqlTdeCertificate Tde(Action<SqlTdeCertificateSpec> configure)
        {
            var spec = new SqlTdeCertificateSpec
            {
                Target = new TargetSelector { Instances = ["x"] },
                Certificate = new CertificateSpec
                {
                    LogicalName = "tde",
                    Source = new CertificateSourceSpec
                    {
                        Type = "OperatorManaged",
                        OperatorManaged = new OperatorManagedCertificateSourceSpec
                        {
                            ExpiryDate = "2031-01-01",
                            SourceSecretEncryptionPasswordRef = new SecretReference { Name = "pwd", Key = "password" },
                        },
                    },
                },
                Databases = new TdeDatabaseScopeSpec
                {
                    Selector = new DatabaseSelectorSpec { AllUserDatabases = true },
                    Policy = new DatabaseTdePolicySpec(),
                },
            };
            configure(spec);
            return new SqlTdeCertificate { Spec = spec };
        }

        var validator = new SqlTdeCertificateValidator();
        Assert.True(validator.Create(Tde(_ => { }), false).Valid);
        Assert.False(validator.Create(Tde(s => s.Certificate.Source.OperatorManaged!.KeyLength = 4096), false).Valid);
        Assert.False(validator.Create(Tde(s => s.Rotation = new RotationSpec { Retirement = new RetirementPolicySpec { ApprovalMode = "AutomaticWhenSafe" } }), false).Valid);
    }

    // ── ───────────────────────────────────────

    [Theory]
    [InlineData("/var/opt/mssql/data/x\n[traceflag]\ntraceflag0 = 3608", false)]
    [InlineData("/var/opt/mssql/data/../../../etc", false)]
    [InlineData("/var/opt/mssql/tde-backups/defaults", true)]
    [InlineData("/var/opt/mssql/certbackups", false)]
    public void Configuration_ServerSettingPaths(string path, bool valid)
    {
        var config = new SqlConfiguration { Spec = new SqlConfigurationSpec { ServerSettings = new ServerSettingsSpec { DefaultBackupPath = path } } };
        Assert.Equal(valid, new SqlConfigurationValidator().Create(config, false).Valid);
    }

    [Fact]
    public void MssqlConf_RefusesAPathWithALineBreak()
    {
        var spec = new SqlConfigurationSpec { ServerSettings = new ServerSettingsSpec { DefaultDataPath = "/var/opt/mssql/data\nmasterdatafile = /tmp/m.mdf" } };
        Assert.Throws<ArgumentException>(() => new MssqlConfBuilder().Build(spec, null, null));
    }

    // ── ────────────────────────────────────────────────

    [Fact]
    public void SecurityContext_DangerousSettings_AreRefused()
    {
        Assert.NotNull(SecurityContextPolicy.Check(null, new V1SecurityContext { Privileged = true }, "spec"));
        Assert.NotNull(SecurityContextPolicy.Check(null, new V1SecurityContext { Capabilities = new V1Capabilities { Add = ["SYS_ADMIN"] } }, "spec"));
        Assert.NotNull(SecurityContextPolicy.Check(new V1PodSecurityContext { RunAsUser = 0 }, null, "spec"));
        Assert.NotNull(SecurityContextPolicy.Check(new V1PodSecurityContext { SeccompProfile = new V1SeccompProfile { Type = "Unconfined" } }, null, "spec"));
        Assert.NotNull(SecurityContextPolicy.Check(new V1PodSecurityContext { Sysctls = [new V1Sysctl { Name = "kernel.msgmax", Value = "1" }] }, null, "spec"));
    }

    [Fact]
    public void SecurityContext_UsualHardening_IsAccepted()
    {
        Assert.Null(SecurityContextPolicy.Check(null, null, "spec"));
        Assert.Null(SecurityContextPolicy.Check(new V1PodSecurityContext { RunAsUser = 10001, RunAsNonRoot = true },
            new V1SecurityContext { ReadOnlyRootFilesystem = true, AllowPrivilegeEscalation = true, Capabilities = new V1Capabilities { Add = ["IPC_LOCK"] } },
            "spec"));
    }

    [Fact]
    public void Validators_RefuseAPrivilegedPod_AndAnUnlistedRegistry()
    {
        Assert.False(new SqlInstanceValidator().Create(Instance(s => s.SecurityContext = new V1SecurityContext { Privileged = true }), false).Valid);
        Assert.False(new SqlAvailabilityGroupValidator().Create(
            Ag(s => s.InstanceTemplate!.PodSecurityContext = new V1PodSecurityContext { RunAsNonRoot = false }), false).Valid);

        var options = new OperatorOptions();
        var refused = new SqlInstanceValidator(options: options).Create(Instance(s => s.Image = "evil.example.com/mssql:2022"), false);
        Assert.False(refused.Valid);
        Assert.Contains("allowed registry", refused.Message);
    }

    [Fact]
    public void ImagePolicy_Wildcard_LiftsTheRestriction()
        => Assert.True(ImagePolicy.IsAllowed("registry.corp/mssql:2022", ["mcr.microsoft.com/", "*"]));

    // ── ────────────────────────────────────────────────

    [Fact]
    public void StatefulSetSelector_CarriesTheIdentityLabelOnly()
    {
        var builder = new StatefulSetBuilder(new AntiAffinityBuilder(), new TopologySpreadBuilder());
        var sts = builder.Build(Instance(), null);
        Assert.Equal(new Dictionary<string, string> { ["mssql-operator.evariops.com/instance"] = "x" }, sts.Spec.Selector.MatchLabels);

        var agSts = builder.BuildForAg(Ag(), null);
        Assert.Equal(new Dictionary<string, string> { ["mssql-operator.evariops.com/ag"] = "ag" }, agSts.Spec.Selector.MatchLabels);
    }

    [Fact]
    public void SelectorChanged_DetectsTheFormerSelector()
    {
        static V1StatefulSet Sts(Dictionary<string, string>? labels)
            => new() { Spec = new V1StatefulSetSpec { Selector = labels is null ? null : new V1LabelSelector { MatchLabels = labels } } };

        var identity = new Dictionary<string, string> { ["mssql-operator.evariops.com/instance"] = "x" };
        var former = new Dictionary<string, string>(identity) { ["mssql-operator.evariops.com/version"] = "2019" };
        Assert.True(StatefulSetRecreation.SelectorChanged(Sts(former), Sts(identity)));
        Assert.False(StatefulSetRecreation.SelectorChanged(Sts(new(identity)), Sts(identity)));
        Assert.False(StatefulSetRecreation.SelectorChanged(Sts(null), Sts(identity)));
    }

    // ── ────────────────────────────────────────────────

    [Fact]
    public void MachineKeyVolume_ProjectsTheReferencedKey()
    {
        var reference = new SecretReference { Name = "migrated", Key = "key.bin" };
        Assert.Equal("key.bin", MachineKeyManager.SecretKeyFor("migrated", reference));
        Assert.Equal("machine-key", MachineKeyManager.SecretKeyFor("x-machine-key", reference));
        Assert.Equal("machine-key", MachineKeyManager.SecretKeyFor("migrated", new SecretReference { Name = "migrated" }));

        var builder = new StatefulSetBuilder(new AntiAffinityBuilder(), new TopologySpreadBuilder());
        var sts = builder.Build(Instance(s => s.MachineKeySecretRef = reference), null, "migrated");
        var volume = sts.Spec.Template.Spec.Volumes.Single(v => v.Name == "machine-key");
        Assert.Equal("key.bin", volume.Secret.Items.Single().Key);
    }

    // ── ───────────────────────────────────────

    [Fact]
    public void Crd_ImmutabilityRules_ApplyToPresence_AndPatternsAreAnchored()
    {
        var schema = CrdGenerator.Generate(typeof(SqlInstance)).Spec.Versions.Single().Schema.OpenAPIV3Schema;
        var spec = schema.Properties["spec"];
        Assert.Contains(spec.XKubernetesValidations, r => r.Rule.Contains("has(self.mode) == has(oldSelf.mode)", StringComparison.Ordinal));

        var storage = spec.Properties["storage"];
        Assert.Contains(storage.XKubernetesValidations, r => r.Rule == "has(self.sysDb) == has(oldSelf.sysDb)");
        var data = storage.Properties["data"];
        Assert.Null(data.Properties["storageClassName"].XKubernetesValidations);
        Assert.Contains(data.XKubernetesValidations, r => r.Rule.StartsWith("has(self.storageClassName) == has(oldSelf.storageClassName)", StringComparison.Ordinal));

        Assert.Equal("^(ClusterIP|NodePort|LoadBalancer)$", spec.Properties["service"].Properties["type"].Pattern);
    }
}
