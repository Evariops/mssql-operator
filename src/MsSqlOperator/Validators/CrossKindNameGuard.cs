using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Webhooks;

namespace MsSqlOperator.Validators;

/// <summary>
/// Admission-time half of the cross-kind collision defence; the reconcile-time
/// half is <see cref="Services.Kubernetes.ChildOwnershipGuard"/>.
///
/// Kubernetes enforces name uniqueness per KIND, but this operator derives every child's name from
/// the CR's name: a <c>SqlInstance</c> and a <c>SqlAvailabilityGroup</c> both called <c>x</c> in the
/// same namespace fight over the StatefulSet <c>x</c>, the ConfigMap <c>x-mssql-conf</c>, the PVCs
/// <c>data-x-0</c> and the Secret <c>x-machine-key</c>. Names derived with a suffix collide too: a
/// SqlInstance called <c>x-headless</c> owns a Service named exactly like the AG's headless Service.
///
/// Rejecting at admission turns a silent, data-destroying conflict into an error message at
/// <c>kubectl apply</c> time.
/// </summary>
public static class CrossKindNameGuard
{
    /// <summary>Suffixes the operator appends to a CR name to build Service names.</summary>
    private static readonly string[] ServiceSuffixes = ["-headless", "-listener", "-ro"];

    public static async Task<string?> ConflictAsync(IKubernetesClient client, string kind, string name, string ns, CancellationToken ct)
    {
        // 1. A CR of the OTHER kind with the same name owns the same children.
        var otherKind = kind == "SqlInstance" ? "SqlAvailabilityGroup" : "SqlInstance";
        var conflictingName = await ExistsAsync(client, otherKind, name, ns, ct);
        if (conflictingName)
            return $"A {otherKind} named '{name}' already exists in namespace '{ns}'. Both kinds derive their " +
                   $"StatefulSet, ConfigMap, PVCs and key Secrets from the resource name, so they would share — and " +
                   $"corrupt — the same children. Choose a different name.";

        // 2. This name collides with a Service the other kind derives from ITS name
        //    (a SqlInstance called 'x-headless' vs an AG called 'x').
        foreach (var suffix in ServiceSuffixes)
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var baseName = name[..^suffix.Length];
            if (baseName.Length == 0)
                continue;
            if (await ExistsAsync(client, "SqlAvailabilityGroup", baseName, ns, ct))
                return $"Name '{name}' collides with the '{suffix}' Service of the SqlAvailabilityGroup '{baseName}' " +
                       $"in namespace '{ns}'. Choose a different name.";
        }

        // 3. The reverse: creating AG 'x' while a SqlInstance 'x-headless' already owns that Service name.
        if (kind == "SqlAvailabilityGroup")
        {
            foreach (var suffix in ServiceSuffixes)
            {
                if (await ExistsAsync(client, "SqlInstance", name + suffix, ns, ct))
                    return $"A SqlInstance named '{name}{suffix}' already exists in namespace '{ns}' and owns the Service " +
                           $"name this availability group needs for its '{suffix.TrimStart('-')}' endpoint. Choose a different name.";
            }
        }

        return null;
    }

    private static async Task<bool> ExistsAsync(IKubernetesClient client, string kind, string name, string ns, CancellationToken ct)
    {
        try
        {
            return kind switch
            {
                "SqlInstance" => await client.GetAsync<SqlInstance>(name, ns, ct) is not null,
                "SqlAvailabilityGroup" => await client.GetAsync<SqlAvailabilityGroup>(name, ns, ct) is not null,
                _ => false,
            };
        }
        catch
        {
            // The webhook must not fail closed on a transient API error: the reconcile-time
            // ChildOwnershipGuard still refuses to touch a foreign child.
            return false;
        }
    }
}
