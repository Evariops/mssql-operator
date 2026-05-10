using k8s.Models;

namespace MsSqlOperator.Services.Kubernetes;

public interface IMachineKeyManager
{
    byte[] Generate();
    Task<string> EnsureMachineKeySecretAsync(string name, string ns, V1OwnerReference owner, string? existingSecretName, string? existingSecretKey, CancellationToken ct);
}
