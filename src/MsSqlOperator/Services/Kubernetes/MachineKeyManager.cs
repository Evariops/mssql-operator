using System.Security.Cryptography;
using k8s.Autorest;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Raised when an explicitly referenced machine-key Secret is absent. The scenario
/// behind machineKeySecretRef is migration: generating a replacement key would silently make
/// every existing encrypted artifact undecryptable — the only safe answer is to stop.
/// </summary>
public class MachineKeySecretMissingException(string message) : InvalidOperationException(message);

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
        // If user provides their own machine key secret (migration scenario), use it as-is.
        // When the reference is explicit and broken, fail — never generate a
        // replacement key, it would make the existing encrypted data undecryptable.
        if (existingSecretName != null)
        {
            var existing = await kubernetesClient.GetAsync<V1Secret>(existingSecretName, ns, ct);
            if (existing?.Data != null && existing.Data.ContainsKey(existingSecretKey ?? SecretKey))
            {
                logger.LogInformation("Using existing machine key secret {Secret} for {Name}", existingSecretName, name);
                return existingSecretName;
            }

            throw new MachineKeySecretMissingException(
                $"machineKeySecretRef '{existingSecretName}' (key '{existingSecretKey ?? SecretKey}') not found in namespace '{ns}'. " +
                "Fix the reference or create the Secret; the operator will not generate a replacement key.");
        }

        var secretName = $"{name}{MachineKeySuffix}";
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);

        if (secret?.Data != null && secret.Data.ContainsKey(SecretKey))
        {
            // A SqlInstance and a SqlAvailabilityGroup can share a name, hence this
            // Secret. Silently reusing another kind's machine key ties two resources to one key.
            EnsureNotForeign(secret, owner, secretName, ns);

            // Adopt legacy secrets that still carry an ownerReference — with it, deleting
            // the CR garbage-collects the machine key while the PVCs (and the SMK/TDE material they
            // hold, encrypted with that key) survive. The data then becomes undecryptable forever.
            await StripOwnerReferencesAsync(secret, owner, ct);

            logger.LogDebug("Machine key secret {Secret} already exists for {Name}", secretName, name);
            return secretName;
        }

        // Generate and create. Deliberately WITHOUT an ownerReference.
        var machineKey = Generate();
        var newSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    // Managed-by for inventory (Secrets are excluded from the owned-resource watch).
                    [Operator.OperatorLabels.ManagedBy] = Operator.OperatorLabels.ManagedByValue,
                    [Operator.OperatorLabels.OwnerKind] = owner.Kind,
                    [Operator.OperatorLabels.OwnerUid] = owner.Uid ?? string.Empty,
                    [Operator.OperatorLabels.RetainedKeyMaterial] = "true",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false",
                    [RetentionAnnotation] =
                        "This Secret holds the SQL Server machine key that encrypts the Service Master Key on the " +
                        "PersistentVolumes. It carries NO ownerReference on purpose: deleting the custom resource must " +
                        "not garbage-collect it while the data volumes survive, or the databases become undecryptable. " +
                        "Delete it explicitly, after the PVCs.",
                },
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                [SecretKey] = machineKey
            }
        };

        try
        {
            await kubernetesClient.CreateAsync(newSecret, ct);
            logger.LogInformation("Created machine key secret {Secret} for {Name} (retained: no ownerReference)", secretName, name);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Concurrent creation (get-or-create): the existing secret wins — never overwrite
            // key material another writer just persisted.
            logger.LogInformation("Machine key secret {Secret} was created concurrently; using it", secretName);
        }
        return secretName;
    }

    internal const string RetentionAnnotation = "mssql-operator.evariops.com/retention-notice";

    private static void EnsureNotForeign(V1Secret secret, V1OwnerReference owner, string secretName, string ns)
    {
        if (secret.Metadata is null)
            return;

        string? ownerKindLabel = null;
        secret.Metadata.Labels?.TryGetValue(Operator.OperatorLabels.OwnerKind, out ownerKindLabel);
        var ownerRefKind = secret.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true)?.Kind;
        var existingKind = ownerKindLabel ?? ownerRefKind;

        if (!string.IsNullOrEmpty(existingKind) && existingKind != owner.Kind)
            throw new MachineKeySecretMissingException(
                $"Machine key secret '{secretName}' in namespace '{ns}' belongs to a {existingKind}, not to this {owner.Kind}. " +
                "Two resources of different kinds cannot share a machine key (a SqlInstance and a SqlAvailabilityGroup may " +
                $"legally share the name '{owner.Name}'). Rename this {owner.Kind}.");
    }

    /// <summary>Removes ownerReferences from an existing key Secret so it is no longer garbage
    /// collected with the CR, and stamps the ownership labels used from now on.</summary>
    private async Task StripOwnerReferencesAsync(V1Secret secret, V1OwnerReference owner, CancellationToken ct)
    {
        secret.Metadata ??= new V1ObjectMeta();
        var hasOwnerRefs = secret.Metadata.OwnerReferences is { Count: > 0 };
        var labels = secret.Metadata.Labels ??= new Dictionary<string, string>();
        var alreadyStamped = labels.ContainsKey(Operator.OperatorLabels.RetainedKeyMaterial);
        if (!hasOwnerRefs && alreadyStamped)
            return;

        secret.Metadata.OwnerReferences = null;
        labels[Operator.OperatorLabels.OwnerKind] = owner.Kind;
        labels[Operator.OperatorLabels.OwnerUid] = owner.Uid ?? string.Empty;
        labels[Operator.OperatorLabels.RetainedKeyMaterial] = "true";

        try
        {
            await kubernetesClient.UpdateAsync(secret, ct);
            if (hasOwnerRefs)
                logger.LogWarning(
                    "Removed the ownerReference from machine key secret {Secret}: key material must survive the deletion " +
                    "of its custom resource, because the PersistentVolumes it encrypts do", secret.Metadata.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not detach the ownerReference from machine key secret {Secret}", secret.Metadata.Name);
        }
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
