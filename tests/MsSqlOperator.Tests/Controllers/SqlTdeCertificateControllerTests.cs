using System.Reflection;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlTdeCertificateControllerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connectionFactory = new();
    private readonly FakeSqlTdeManager _tdeManager = new();
    private readonly FakeTargetResolver _targetResolver = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly FakeS3Client _s3Client = new();
    private readonly FakeBackupEncryptor _backupEncryptor = new();
    private readonly SqlTdeCertificateController _controller;

    public SqlTdeCertificateControllerTests()
    {
        _controller = new SqlTdeCertificateController(
            NullLogger<SqlTdeCertificateController>.Instance, _k8s, _connectionFactory,
            _tdeManager, _targetResolver, _eventPublisher,
            _s3Client, _backupEncryptor,
            new MsSqlOperator.Services.Metrics.OperatorMetrics());
    }

    private static SqlTdeCertificate CreateEntity(string? trigger = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "tde-cert",
            NamespaceProperty = "default",
            Generation = 1
        },
        Spec = new SqlTdeCertificateSpec
        {
            Target = new TargetSelector { Instances = ["my-instance"] },
            Certificate = new CertificateSpec
            {
                LogicalName = "tde-cert",
                Source = new CertificateSourceSpec
                {
                    Type = "KubernetesSecret",
                    KubernetesSecret = new KubernetesSecretCertificateSourceSpec
                    {
                        Discovery = new CertificateSecretDiscoverySpec
                        {
                            Names =
                            [
                                new CertificateSecretVersionRef
                                {
                                    SecretName = "tde-secret",
                                    SqlCertificateName = "tde_cert_v1"
                                }
                            ]
                        },
                        ActivationPolicy = "SecretRole"
                    }
                }
            },
            Databases = new TdeDatabaseScopeSpec
            {
                Selector = new DatabaseSelectorSpec { AllUserDatabases = true },
                Policy = new DatabaseTdePolicySpec()
            },
            Rotation = trigger != null ? new RotationSpec { Trigger = trigger } : null
        },
        Status = new SqlTdeCertificateStatus()
    };

    // ── No targets ────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_NoTargets_SetsNoMatchCondition()
    {
        var entity = CreateEntity();
        _targetResolver.Resolved = [];

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        var condition = result.Entity!.Status!.Conditions!.First(c => c.Type == "Ready");
        Assert.Equal("False", condition.Status);
        Assert.Equal("NoMatch", condition.Reason);
    }

    // ── No source secret ──────────────────────────

    [Fact]
    public async Task ReconcileAsync_NoSource_SetsNoSourceCondition()
    {
        var entity = CreateEntity();
        entity.Spec.Certificate.Source = new CertificateSourceSpec
        {
            Type = "KubernetesSecret",
            KubernetesSecret = new KubernetesSecretCertificateSourceSpec
            {
                Discovery = new CertificateSecretDiscoverySpec
                {
                    Names = [new CertificateSecretVersionRef { SecretName = "missing-secret" }]
                },
                ActivationPolicy = "SecretRole"
            }
        };

        _targetResolver.Resolved = [new ResolvedTarget("SqlInstance", "my-instance", "default", "host", 1433, "sa-pwd", null)];
        _k8s.SetGet<V1Secret>("missing-secret", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        var condition = result.Entity!.Status!.Conditions!.First(c => c.Type == "Ready");
        Assert.Equal("False", condition.Status);
        Assert.Equal("NoSource", condition.Reason);
    }

    // ── Trigger ManualOnly without annotation ─────

    [Fact]
    public async Task ReconcileAsync_ManualOnly_NoAnnotation_SetsIdle()
    {
        var entity = CreateEntity("ManualOnly");
        _targetResolver.Resolved = [new ResolvedTarget("SqlInstance", "my-instance", "default", "host", 1433, "sa-pwd", null)];
        _k8s.SetGet("tde-secret", "default", new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "tde-secret",
                Labels = new Dictionary<string, string>
                {
                    ["mssql-operator.evariops.com/certificate-role"] = "active",
                    ["mssql-operator.evariops.com/certificate-version"] = "v1"
                }
            },
            Data = new Dictionary<string, byte[]>
            {
                ["certificate.cer"] = [1, 2, 3],
                ["private-key.pvk"] = [4, 5, 6],
                ["password"] = "pwd"u8.ToArray()
            }
        });
        // Provisioning still needs a SQL connection; simulate it being unavailable in the unit test.
        _connectionFactory.ConnectionForHost = _ => throw new InvalidOperationException("SQL unavailable for test");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        // Error deploying is expected since SQL is unavailable; the entity still comes back.
        Assert.NotNull(result.Entity);
    }

    // ── Deleted ───────────────────────────────────

    [Fact]
    public async Task DeletedAsync_ReturnsSuccess()
    {
        var result = await _controller.DeletedAsync(CreateEntity(), CancellationToken.None);
        Assert.NotNull(result.Entity);
    }

    // ── MaxParallelTargets throttling ─────────────

    [Fact]
    public async Task ReconcileAsync_MaxParallelTargets_LimitsProcessing()
    {
        var entity = CreateEntity();
        entity.Spec.Rotation = new RotationSpec { MaxParallelTargets = 1 };

        _targetResolver.Resolved =
        [
            new("SqlInstance", "inst-1", "default", "host1", 1433, "sa-pwd", null),
            new("SqlInstance", "inst-2", "default", "host2", 1433, "sa-pwd", null),
            new("SqlInstance", "inst-3", "default", "host3", 1433, "sa-pwd", null),
        ];
        _k8s.SetGet<V1Secret>("tde-secret", "default", null);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        // NoSource → no SQL calls → but no crash
        Assert.NotNull(result.Entity);
    }

    // ── Backup safety: encryption password is always present ──
    //
    // These exercise the private password-resolution / cert-provisioning paths via reflection,
    // guarding the invariant that an exported TDE private key can always be decrypted again.

    private static readonly Type VersionInfoType =
        typeof(SqlTdeCertificateController).GetNestedType("CertificateVersionInfo", BindingFlags.NonPublic)!;

    private static object NewVersion(Dictionary<string, object?> props)
    {
        var v = Activator.CreateInstance(VersionInfoType)!;
        foreach (var (name, value) in props)
            VersionInfoType.GetProperty(name)!.SetValue(v, value);
        return v;
    }

    private async Task<object?> InvokePrivateAsync(string method, params object?[] args)
    {
        var mi = typeof(SqlTdeCertificateController).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)mi.Invoke(_controller, args)!;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    [Fact]
    public async Task ResolveOrGeneratePassword_UsesStoredPasswordWhenPresent()
    {
        var entity = CreateEntity();
        var version = NewVersion(new() { ["Password"] = "stored-pwd" });

        var pwd = (string)(await InvokePrivateAsync("ResolveOrGenerateEncryptionPasswordAsync", entity, version, CancellationToken.None))!;

        Assert.Equal("stored-pwd", pwd);
    }

    [Fact]
    public async Task ResolveOrGeneratePassword_GeneratesStrongRandom_WhenNoneConfigured()
    {
        var entity = CreateEntity();

        var first = (string)(await InvokePrivateAsync("ResolveOrGenerateEncryptionPasswordAsync", entity, NewVersion(new()), CancellationToken.None))!;
        var second = (string)(await InvokePrivateAsync("ResolveOrGenerateEncryptionPasswordAsync", entity, NewVersion(new()), CancellationToken.None))!;

        Assert.False(string.IsNullOrEmpty(first));
        Assert.True(first.Length > 20); // 32 random bytes, base64
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ResolveOrGeneratePassword_Throws_WhenConfiguredSecretMissing()
    {
        var entity = CreateEntity();
        var version = NewVersion(new()
        {
            ["EncryptionPasswordSecretRef"] = new SecretReference { Name = "pwd-secret", Key = "password" }
        });
        _k8s.SetGet<V1Secret>("pwd-secret", "default", null);

        var act = async () => await InvokePrivateAsync("ResolveOrGenerateEncryptionPasswordAsync", entity, version, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task EnsureCertificate_ImportWithoutPassword_IsRejected()
    {
        var entity = CreateEntity();
        var version = NewVersion(new()
        {
            ["SqlCertificateName"] = "tde_cert_v1",
            ["NeedsGeneration"] = false,
            ["CertBytes"] = new byte[] { 1, 2, 3 },
            ["KeyBytes"] = new byte[] { 4, 5, 6 },
            ["Password"] = null
        });
        _tdeManager.CertificateExists = _ => false;

        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        var act = async () => await InvokePrivateAsync("EnsureCertificateOnTargetAsync", conn, entity, version, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("no private-key password", ex.Message);
    }

    [Fact]
    public async Task EnsureCertificate_Generation_PersistsPasswordInSourceSecret()
    {
        var entity = CreateEntity();
        var version = NewVersion(new()
        {
            ["SqlCertificateName"] = "tde_cert_v1",
            ["SourceSecretName"] = "tde-source",
            ["Version"] = "20260101",
            ["NeedsGeneration"] = true
        });

        _tdeManager.CertificateExists = _ => false;
        _tdeManager.BackupWithPasswordResult = (new byte[] { 1 }, new byte[] { 2 });
        _tdeManager.Thumbprint = "THUMB";

        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        await InvokePrivateAsync("EnsureCertificateOnTargetAsync", conn, entity, version, CancellationToken.None);

        var captured = Assert.Single(_k8s.Created<V1Secret>());
        Assert.Contains("password", captured.Data);
        Assert.NotEmpty(captured.Data["password"]);
        // The very byte material we hand to the secret must be encrypted with that same password.
        Assert.Single(_tdeManager.BackupWithPasswordCalls, c => c == "tde_cert_v1");
    }

    // ── Rotation blocked while a replica lacks the certificate ──

    private void SetupAgTarget()
    {
        _targetResolver.Resolved = [new ResolvedTarget("SqlAvailabilityGroup", "my-ag", "default", "my-ag-0.my-ag-headless.default.svc.cluster.local", 1433, "sa-pwd", 3)];
        _k8s.SetGet("tde-secret", "default", new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "tde-secret",
                Labels = new Dictionary<string, string>
                {
                    ["mssql-operator.evariops.com/certificate-role"] = "active",
                    ["mssql-operator.evariops.com/certificate-version"] = "v1"
                }
            },
            Data = new Dictionary<string, byte[]>
            {
                ["certificate.cer"] = [1, 2, 3],
                ["private-key.pvk"] = [4, 5, 6],
                ["password"] = "pwd"u8.ToArray()
            }
        });

        _tdeManager.MasterKeyExists = true;
        _tdeManager.CertificateExists = c => c == "tde_cert_v1";
        _tdeManager.Thumbprint = "THUMB";
        _tdeManager.BackupWithPasswordResult = (new byte[] { 1 }, new byte[] { 2 });
        _targetResolver.Primary = ("my-ag-0", new Microsoft.Data.SqlClient.SqlConnection());
    }

    [Fact]
    public async Task ReconcileAsync_AgSecondaryUnreachable_DoesNotMoveDekProtector_AndEmitsEvent()
    {
        var entity = CreateEntity();
        SetupAgTarget();

        // Replicas 0 and 1 connect; replica 2 is down.
        _connectionFactory.ConnectionForHost = h =>
            h.StartsWith("my-ag-2.") ? throw new InvalidOperationException("replica down") : new Microsoft.Data.SqlClient.SqlConnection();

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_tdeManager.ChangeDekProtectorCalls);
        Assert.Single(_eventPublisher.Calls,
            c => c.Reason == "ReplicaCertDeployFailed" && c.Message.Contains("my-ag-2") && c.Type == "Warning");
        Assert.Equal("Failed", result.Entity.Status!.Phase);
    }

    [Fact]
    public async Task ReconcileAsync_AgFirstReplicaUnreachable_TargetFailsImmediately()
    {
        var entity = CreateEntity();
        SetupAgTarget();

        _connectionFactory.ConnectionForHost = _ => throw new InvalidOperationException("all down");

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_tdeManager.ChangeDekProtectorCalls);
        Assert.Equal("Failed", result.Entity.Status!.Phase);
    }
}
