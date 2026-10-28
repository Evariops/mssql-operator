using System.Security.Cryptography;
using k8s.Autorest;
using k8s.Models;
using MsSqlOperator.Entities.Common;
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

    /// <summary>Annotation of a generated machine key Secret: the sha256 of the key it was created
    /// with. A Secret whose key no longer matches it was overwritten.</summary>
    public const string FingerprintAnnotation = "mssql-operator.evariops.com/machine-key-sha256";

    public static string Fingerprint(byte[] key) => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

    public async Task<MachineKeyInfo> EnsureMachineKeySecretAsync(string name, string ns, V1OwnerReference owner,
        string? existingSecretName, string? existingSecretKey, CancellationToken ct)
    {
        // If user provides their own machine key secret (migration scenario), use it as-is.
        // When the reference is explicit and broken, fail — never generate a
        // replacement key, it would make the existing encrypted data undecryptable.
        if (existingSecretName != null)
        {
            // An omitted key arrives as "" (SecretReference.Key's default), not null.
            var key = string.IsNullOrEmpty(existingSecretKey) ? SecretKey : existingSecretKey;
            var existing = await kubernetesClient.GetAsync<V1Secret>(existingSecretName, ns, ct);
            if (existing?.Data != null && existing.Data.TryGetValue(key, out var referenced))
            {
                logger.LogInformation("Using existing machine key secret {Secret} for {Name}", existingSecretName, name);
                return new MachineKeyInfo(existingSecretName, Fingerprint(referenced), StampOf(existing));
            }

            throw new MachineKeySecretMissingException(
                $"machineKeySecretRef '{existingSecretName}' (key '{key}') not found in namespace '{ns}'. " +
                "Fix the reference or create the Secret; the operator will not generate a replacement key.");
        }

        var secretName = $"{name}{MachineKeySuffix}";
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);

        if (secret?.Data != null && secret.Data.TryGetValue(SecretKey, out var current))
        {
            // A SqlInstance and a SqlAvailabilityGroup can share a name, hence this
            // Secret. Silently reusing another kind's machine key ties two resources to one key.
            EnsureNotForeign(secret, owner, secretName, ns);

            // Adopt legacy secrets that still carry an ownerReference — with it, deleting
            // the CR garbage-collects the machine key while the PVCs (and the SMK/TDE material they
            // hold, encrypted with that key) survive. The data then becomes undecryptable forever.
            await StripOwnerReferencesAsync(secret, owner, ct);

            logger.LogDebug("Machine key secret {Secret} already exists for {Name}", secretName, name);
            return new MachineKeyInfo(secretName, Fingerprint(current), StampOf(secret));
        }

        // A missing Secret next to existing data volumes is a lost key, not a new resource. A
        // new key would make the Service Master Key on those volumes undecryptable for good.
        var volumes = await ExistingDataVolumesAsync(name, ns, ct);
        if (volumes.Count > 0)
            throw new MachineKeySecretMissingException(
                $"Machine key Secret '{secretName}' is missing but data volumes exist ({string.Join(", ", volumes)}): " +
                "restore the machine key Secret the data was created with. The operator will not generate a key over existing data.");

        // Generate and create. Deliberately WITHOUT an ownerReference.
        var machineKey = Generate();
        var fingerprint = Fingerprint(machineKey);
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
                    [FingerprintAnnotation] = fingerprint,
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
            var winner = await kubernetesClient.GetAsync<V1Secret>(secretName, ns, ct);
            if (winner?.Data != null && winner.Data.TryGetValue(SecretKey, out var winnerKey))
                return new MachineKeyInfo(secretName, Fingerprint(winnerKey), StampOf(winner));
        }
        return new MachineKeyInfo(secretName, fingerprint, fingerprint);
    }

    /// <summary>
    /// Compares the key mounted now with the one recorded in the status (or, failing that,
    /// stamped on the Secret at its creation). Returns the fingerprint to keep recording — always the
    /// original one — and, when the key changed, why.
    /// </summary>
    public static (string Recorded, string? Changed) CompareFingerprint(string? recorded, MachineKeyInfo info)
    {
        var expected = recorded ?? info.StampedSha256 ?? info.Sha256;
        var overwritten = info.StampedSha256 is not null && info.StampedSha256 != info.Sha256;
        if (expected == info.Sha256 && !overwritten)
            return (expected, null);
        var original = overwritten ? info.StampedSha256! : expected;
        return (expected,
            $"The machine key in Secret '{info.SecretName}' (sha256 {original[..Math.Min(12, original.Length)]}… expected, " +
            $"{info.Sha256[..12]}… found) is not the one the data was created with. Restore the original Secret: the " +
            "Service Master Key on the volumes can only be decrypted with it.");
    }

    private static string? StampOf(V1Secret secret)
        => secret.Metadata?.Annotations is { } annotations && annotations.TryGetValue(FingerprintAnnotation, out var stamp) ? stamp : null;

    /// <summary>The PVCs of the StatefulSet named <paramref name="name"/> (one per volume claim
    /// template and ordinal), matched exactly so that another StatefulSet's PVCs never count.</summary>
    private async Task<List<string>> ExistingDataVolumesAsync(string name, string ns, CancellationToken ct)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            $"^({string.Join("|", StatefulSetBuilder.VolumeClaimTemplateNames)})-{System.Text.RegularExpressions.Regex.Escape(name)}-\\d+$");
        var claims = await kubernetesClient.ListAsync<V1PersistentVolumeClaim>(ns, ct: ct);
        return claims.Select(c => c.Metadata?.Name).Where(n => n is not null && pattern.IsMatch(n)).Select(n => n!).OrderBy(n => n).ToList();
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not detach the ownerReference from machine key secret {Secret}", secret.Metadata.Name);
        }
    }

    /// <summary>The key holding the machine key in <paramref name="secretName"/>: the one
    /// <paramref name="reference"/> names when the Secret is the referenced one (the mount
    /// always projected <c>machine-key</c>, so a migration Secret with another key left the pod in
    /// ContainerCreating), else the key of the Secret the operator generates.</summary>
    public static string SecretKeyFor(string secretName, SecretReference? reference)
        => reference is not null && reference.Name == secretName && !string.IsNullOrEmpty(reference.Key) ? reference.Key : SecretKey;

    public static V1Volume BuildMachineKeyVolume(string secretName, string key = SecretKey)
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
                    new V1KeyToPath { Key = key, Path = "machine-key" }
                ]
            }
        };
    }

    /// <summary>Where SQL Server reads its machine key. The key used to be mounted under
    /// /var/opt/mssql/.system, a path the engine does not read — it generated its own key in
    /// /var/opt/mssql/secrets (an emptyDir), a new one in every pod, so a recreated pod could no longer
    /// decrypt its Service Master Key nor the private key of its HADR endpoint certificate.</summary>
    public const string MachineKeyPath = "/var/opt/mssql/secrets/machine-key";

    public static V1VolumeMount BuildMachineKeyMount()
    {
        return new V1VolumeMount
        {
            Name = "machine-key",
            MountPath = MachineKeyPath,
            SubPath = "machine-key",
            ReadOnlyProperty = true
        };
    }
}
