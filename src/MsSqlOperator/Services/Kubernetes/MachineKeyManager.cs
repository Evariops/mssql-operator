using System.Security.Cryptography;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Kubernetes;

public class MachineKeyManager(ILogger<MachineKeyManager> logger, IKubernetesClient kubernetesClient) : IMachineKeyManager
{
    private const string MachineKeySuffix = "-machine-key";
    private const string SecretKey = "machine-key";

    public byte[] Generate()
    {
        var key = new byte[44];
        // Header DPAPI Linux — SQL Server on Linux expects this format
        key[0] = 0x01;
        // Bytes 1-11 remain 0x00
        // 32 random bytes for the actual key material
        RandomNumberGenerator.Fill(key.AsSpan(12));
        return key;
    }

    public async Task<string> EnsureMachineKeySecretAsync(string name, string ns, V1OwnerReference owner,
        string? existingSecretName, string? existingSecretKey, CancellationToken ct)
    {
        // If user provides their own machine key secret (migration scenario), use it as-is
        if (existingSecretName != null)
        {
            var existing = await kubernetesClient.GetAsync<V1Secret>(existingSecretName, ns, ct);
            if (existing?.Data != null && existing.Data.ContainsKey(existingSecretKey ?? SecretKey))
            {
                logger.LogInformation("Using existing machine key secret {Secret} for {Name}", existingSecretName, name);
                return existingSecretName;
            }

            logger.LogWarning("Referenced machine key secret {Secret} not found or missing key, generating new one", existingSecretName);
        }

        var secretName = $"{name}{MachineKeySuffix}";
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);

        if (secret?.Data != null && secret.Data.ContainsKey(SecretKey))
        {
            logger.LogDebug("Machine key secret {Secret} already exists for {Name}", secretName, name);
            return secretName;
        }

        // Generate and create
        var machineKey = Generate();
        var newSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                OwnerReferences = [owner],
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false"
                }
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                [SecretKey] = machineKey
            }
        };

        await kubernetesClient.CreateAsync(newSecret, ct);
        logger.LogInformation("Created machine key secret {Secret} for {Name}", secretName, name);
        return secretName;
    }

    public static V1Volume BuildMachineKeyVolume(string secretName)
    {
        return new V1Volume
        {
            Name = "machine-key",
            Secret = new V1SecretVolumeSource
            {
                SecretName = secretName,
                DefaultMode = 384, // 0600 in octal
                Items =
                [
                    new V1KeyToPath { Key = SecretKey, Path = "machine-key" }
                ]
            }
        };
    }

    public static V1VolumeMount BuildMachineKeyMount()
    {
        return new V1VolumeMount
        {
            Name = "machine-key",
            MountPath = "/var/opt/mssql/.system/machine-key",
            SubPath = "machine-key",
            ReadOnlyProperty = true
        };
    }
}
