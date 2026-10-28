using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class MachineKeyManagerTests
{
    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly MachineKeyManager _manager;

    public MachineKeyManagerTests()
    {
        _manager = new MachineKeyManager(NullLogger<MachineKeyManager>.Instance, _k8sClient);
    }

    [Fact]
    public void Generate_Returns44ByteKey()
    {
        var key = _manager.Generate();

        Assert.Equal(44, key.Count());
    }

    [Fact]
    public void Generate_HasCorrectDpapiHeader()
    {
        var key = _manager.Generate();

        Assert.Equal(0x01, key[0]);
        for (var i = 1; i < 12; i++)
            Assert.True(key[i] == 0x00, $"byte {i} should be 0x00");
    }

    [Fact]
    public void Generate_HasRandomPayload()
    {
        var key1 = _manager.Generate();
        var key2 = _manager.Generate();

        // The random portion (bytes 12-43) should differ
        Assert.NotEqual(key2.AsSpan(12).ToArray(), key1.AsSpan(12).ToArray());
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_ExistingSecret_ReturnsSecretName()
    {
        _k8sClient.SetGet("test-machine-key", "default", new V1Secret
        {
            Data = new Dictionary<string, byte[]> { ["machine-key"] = new byte[44] }
        });

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None);

        Assert.Equal("test-machine-key", result.SecretName);
        Assert.Empty(_k8sClient.Created<V1Secret>());
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_NoExisting_CreatesSecret()
    {
        _k8sClient.SetGet<V1Secret>("test-machine-key", "default", null);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None);

        Assert.Equal("test-machine-key", result.SecretName);
        var created = Assert.Single(_k8sClient.Created<V1Secret>());
        Assert.Equal("test-machine-key", created.Metadata.Name);
        Assert.True(created.Data!.ContainsKey("machine-key"));
        Assert.Equal(44, created.Data["machine-key"].Length);
        Assert.Equal("Delete=false", created.Metadata.Annotations!["argocd.argoproj.io/sync-options"]);
        // The Secret carries the fingerprint of the key it was created with.
        var fingerprint = MachineKeyManager.Fingerprint(created.Data["machine-key"]);
        Assert.Equal(fingerprint, created.Metadata.Annotations[MachineKeyManager.FingerprintAnnotation]);
        Assert.Equal(fingerprint, result.Sha256);
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_SecretMissingButDataVolumesExist_NoKeyIsGenerated()
    {
        // A missing Secret next to existing PVCs is a lost key, not a new resource.
        _k8sClient.SetGet<V1Secret>("test-machine-key", "default", null);
        _k8sClient.SetList<V1PersistentVolumeClaim>(
        [
            new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "data-test-0" } },
            new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "data-other-test-0" } },
        ]);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var ex = await Assert.ThrowsAsync<MachineKeySecretMissingException>(() =>
            _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None));

        Assert.Contains("data-test-0", ex.Message);
        Assert.DoesNotContain("data-other-test-0", ex.Message);
        Assert.Empty(_k8sClient.Created<V1Secret>());
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_OnlyAnotherStatefulSetsVolumes_GeneratesTheKey()
    {
        _k8sClient.SetGet<V1Secret>("test-machine-key", "default", null);
        _k8sClient.SetList<V1PersistentVolumeClaim>(
            [new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "data-other-test-0" } }]);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        await _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None);

        Assert.Single(_k8sClient.Created<V1Secret>());
    }

    [Theory]
    [InlineData(null, "aaaa", null, "aaaa", false)]          // first sight: recorded
    [InlineData("aaaa", "aaaa", "aaaa", "aaaa", false)]      // unchanged
    [InlineData("aaaa", "bbbb", null, "aaaa", true)]         // Secret replaced by another key
    [InlineData(null, "bbbb", "aaaa", "aaaa", true)]         // key overwritten in place, status lost
    [InlineData("aaaa", "aaaa", "bbbb", "aaaa", true)]       // stamp no longer matches the key
    public void CompareFingerprint_RecordsTheOriginal_AndReportsAChange(string? recorded, string current, string? stamped,
        string expectedRecord, bool changed)
    {
        var (record, message) = MachineKeyManager.CompareFingerprint(recorded?.PadRight(64, '0'),
            new MachineKeyInfo("test-machine-key", current.PadRight(64, '0'), stamped?.PadRight(64, '0')));

        Assert.Equal(expectedRecord.PadRight(64, '0'), record);
        Assert.Equal(changed, message is not null);
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_WithExistingSecretRef_UsesIt()
    {
        _k8sClient.SetGet("my-existing-secret", "default", new V1Secret
        {
            Data = new Dictionary<string, byte[]> { ["my-key"] = new byte[44] }
        });

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, "my-existing-secret", "my-key", CancellationToken.None);

        Assert.Equal("my-existing-secret", result.SecretName);
        Assert.Empty(_k8sClient.Created<V1Secret>());
    }

    [Fact]
    public void BuildMachineKeyVolume_CreatesCorrectVolume()
    {
        var volume = MachineKeyManager.BuildMachineKeyVolume("test-machine-key");

        Assert.Equal("machine-key", volume.Name);
        Assert.NotNull(volume.Secret);
        Assert.Equal("test-machine-key", volume.Secret.SecretName);
        Assert.Equal(384, volume.Secret.DefaultMode); // 0600
        Assert.Single(volume.Secret.Items, i => i.Key == "machine-key" && i.Path == "machine-key");
    }

    [Fact]
    public void BuildMachineKeyMount_CreatesCorrectMount()
    {
        var mount = MachineKeyManager.BuildMachineKeyMount();

        Assert.Equal("machine-key", mount.Name);
        Assert.Equal("/var/opt/mssql/secrets/machine-key", mount.MountPath);
        Assert.Equal("machine-key", mount.SubPath);
        Assert.True(mount.ReadOnlyProperty);
    }
}
