using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class MachineKeyManagerTests
{
    private readonly Mock<ILogger<MachineKeyManager>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly MachineKeyManager _manager;

    public MachineKeyManagerTests()
    {
        _manager = new MachineKeyManager(_logger.Object, _k8sClient.Object);
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
        var existingSecret = new V1Secret
        {
            Data = new Dictionary<string, byte[]> { ["machine-key"] = new byte[44] }
        };
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("test-machine-key", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSecret);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None);

        Assert.Equal("test-machine-key", result);
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_NoExisting_CreatesSecret()
    {
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("test-machine-key", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync((V1Secret?)null);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, null, null, CancellationToken.None);

        Assert.Equal("test-machine-key", result);
        _k8sClient.Verify(x => x.CreateAsync(It.Is<V1Secret>(s =>
            s.Metadata.Name == "test-machine-key" &&
            s.Data!.ContainsKey("machine-key") &&
            s.Data["machine-key"].Length == 44 &&
            s.Metadata.Annotations!["argocd.argoproj.io/sync-options"] == "Delete=false"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureMachineKeySecretAsync_WithExistingSecretRef_UsesIt()
    {
        var existingSecret = new V1Secret
        {
            Data = new Dictionary<string, byte[]> { ["my-key"] = new byte[44] }
        };
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("my-existing-secret", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSecret);

        var owner = new V1OwnerReference { Name = "test", Uid = "uid-1" };
        var result = await _manager.EnsureMachineKeySecretAsync("test", "default", owner, "my-existing-secret", "my-key", CancellationToken.None);

        Assert.Equal("my-existing-secret", result);
        _k8sClient.Verify(x => x.CreateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()), Times.Never);
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
        Assert.Equal("/var/opt/mssql/.system/machine-key", mount.MountPath);
        Assert.Equal("machine-key", mount.SubPath);
        Assert.True(mount.ReadOnlyProperty);
    }
}
