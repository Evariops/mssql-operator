using k8s.Models;

namespace MsSqlOperator.Services.Kubernetes;

/// <summary>The machine key Secret a resource mounts, and the fingerprints of its key: the
/// sha256 of the key it holds now, and the one stamped on the Secret when it was created (null on a
/// Secret the operator did not create, or created before the stamp existed).</summary>
public sealed record MachineKeyInfo(string SecretName, string Sha256, string? StampedSha256);

public interface IMachineKeyManager
{
    byte[] Generate();
    Task<MachineKeyInfo> EnsureMachineKeySecretAsync(string name, string ns, V1OwnerReference owner, string? existingSecretName, string? existingSecretKey, CancellationToken ct);
}
