using System.Reflection;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Builds the operator's ClusterRole from <see cref="EntityRbacAttribute"/> declarations plus a
/// fixed baseline (leader-election leases, event emission), replacing KubeOps' RBAC generation.
/// </summary>
public static class RbacGenerator
{
    public static V1ClusterRole Generate(IEnumerable<Assembly> assemblies, string roleName = "mssql-operator-role")
    {
        var rules = new List<V1PolicyRule>();

        foreach (var asm in assemblies)
        foreach (var type in asm.GetTypes())
        foreach (var attr in type.GetCustomAttributes<EntityRbacAttribute>())
        {
            var entityAttr = attr.EntityType.GetCustomAttribute<KubernetesEntityAttribute>();
            if (entityAttr is null)
                continue;
            var plural = string.IsNullOrEmpty(entityAttr.PluralName)
                ? entityAttr.Kind.ToLowerInvariant() + "s"
                : entityAttr.PluralName;

            rules.Add(new V1PolicyRule
            {
                ApiGroups = [entityAttr.Group ?? string.Empty],
                Resources = [plural],
                Verbs = Verbs(attr.Verbs),
            });
            // Custom resources also need their /status subresource.
            if (GroupVersionKind.Of(attr.EntityType) is { Group.Length: > 0 })
                rules.Add(new V1PolicyRule
                {
                    ApiGroups = [entityAttr.Group ?? string.Empty],
                    Resources = [$"{plural}/status"],
                    Verbs = ["get", "patch", "update"],
                });
        }

        // Baseline: leases (leader election) and events.
        rules.Add(new V1PolicyRule
        {
            ApiGroups = ["coordination.k8s.io"],
            Resources = ["leases"],
            Verbs = ["get", "list", "watch", "create", "update", "patch", "delete"],
        });
        rules.Add(new V1PolicyRule
        {
            ApiGroups = [""],
            Resources = ["events"],
            Verbs = ["create", "patch"],
        });

        return new V1ClusterRole
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind = "ClusterRole",
            Metadata = new V1ObjectMeta { Name = roleName },
            Rules = rules,
        };
    }

    private static IList<string> Verbs(RbacVerb verbs)
    {
        var result = new List<string>();
        if (verbs.HasFlag(RbacVerb.Get)) result.Add("get");
        if (verbs.HasFlag(RbacVerb.List)) result.Add("list");
        if (verbs.HasFlag(RbacVerb.Watch)) result.Add("watch");
        if (verbs.HasFlag(RbacVerb.Create)) result.Add("create");
        if (verbs.HasFlag(RbacVerb.Update)) result.Add("update");
        if (verbs.HasFlag(RbacVerb.Patch)) result.Add("patch");
        if (verbs.HasFlag(RbacVerb.Delete)) result.Add("delete");
        return result;
    }
}
