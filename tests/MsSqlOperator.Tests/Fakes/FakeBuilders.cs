using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IStatefulSetBuilder"/>: returns a configurable result
/// (default: a minimal StatefulSet named after the resource).</summary>
public sealed class FakeStatefulSetBuilder : IStatefulSetBuilder
{
    public V1StatefulSet? Result { get; set; }

    public V1StatefulSet Build(SqlInstance instance, string? configHash, string? machineKeySecretName = null)
        => Result ?? new V1StatefulSet { Metadata = new V1ObjectMeta { Name = instance.Metadata.Name, NamespaceProperty = instance.Metadata.NamespaceProperty }, Spec = new V1StatefulSetSpec() };

    public V1StatefulSet BuildForAg(SqlAvailabilityGroup ag, string? configHash, string? machineKeySecretName = null)
        => Result ?? new V1StatefulSet { Metadata = new V1ObjectMeta { Name = ag.Metadata.Name, NamespaceProperty = ag.Metadata.NamespaceProperty }, Spec = new V1StatefulSetSpec() };
}

/// <summary>Hand-written fake for <see cref="IServiceBuilder"/>: builds a minimal Service named from
/// the requested name (so the controller's apply count and names are realistic).</summary>
public sealed class FakeServiceBuilder : IServiceBuilder
{
    private static V1Service Svc(string name, string ns) => new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns }, Spec = new V1ServiceSpec() };

    public V1Service BuildClusterIp(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference owner) => Svc(name, ns);
    public V1Service BuildHeadless(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference owner) => Svc(name, ns);
    public V1Service Build(string name, string ns, Dictionary<string, string> selector, ServiceSpec serviceSpec, V1OwnerReference owner) => Svc(name, ns);
}

/// <summary>Hand-written fake for <see cref="ICiliumNetworkPolicyBuilder"/>.</summary>
public sealed class FakeCiliumNetworkPolicyBuilder : ICiliumNetworkPolicyBuilder
{
    public GenericCiliumNetworkPolicy BuildForInstance(string name, string ns, Dictionary<string, string> labels, int sqlPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null)
        => new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns } };

    public GenericCiliumNetworkPolicy BuildForAg(string name, string ns, Dictionary<string, string> labels, int sqlPort, int mirroringPort, V1OwnerReference? owner,
        List<string>? allowedNamespaces = null, string? operatorNamespace = null)
        => new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns } };
}

/// <summary>Hand-written fake for <see cref="IMssqlConfBuilder"/>: returns a configurable conf + hash.</summary>
public sealed class FakeMssqlConfBuilder : IMssqlConfBuilder
{
    public string BuildResult { get; set; } = "[EULA]\naccepteula = Y\n";
    public string HashResult { get; set; } = "testhash";

    public string Build(SqlConfigurationSpec? configSpec, MemorySpec? memorySpec, StorageSpec? storageSpec, string? runtimeClassName = null,
        TlsSpec? tls = null) => BuildResult;
    public string ComputeHash(string content) => HashResult;
}

/// <summary>Hand-written fake for <see cref="IMachineKeyManager"/>.</summary>
public sealed class FakeMachineKeyManager : IMachineKeyManager
{
    public byte[] GenerateResult { get; set; } = new byte[44];
    public Func<string, string> SecretNameFor { get; set; } = name => $"{name}-machine-key";

    /// <summary>When set, EnsureMachineKeySecretAsync throws it — simulates a broken/absent
    /// machineKeySecretRef (used by the self-heal regression test).</summary>
    public Exception? ThrowOnEnsure { get; set; }

    public byte[] Generate() => GenerateResult;

    public Task<string> EnsureMachineKeySecretAsync(string name, string ns, V1OwnerReference owner, string? existingSecretName, string? existingSecretKey, CancellationToken ct)
        => ThrowOnEnsure is not null
            ? Task.FromException<string>(ThrowOnEnsure)
            : Task.FromResult(SecretNameFor(name));
}
