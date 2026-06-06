namespace MsSqlOperator.Operator.Abstractions;

/// <summary>RBAC verbs, replacing KubeOps' <c>RbacVerb</c>.</summary>
[Flags]
public enum RbacVerb
{
    None = 0,
    Get = 1 << 0,
    List = 1 << 1,
    Watch = 1 << 2,
    Create = 1 << 3,
    Update = 1 << 4,
    Patch = 1 << 5,
    Delete = 1 << 6,
    All = Get | List | Watch | Create | Update | Patch | Delete,
}

/// <summary>
/// Declares the RBAC permissions a controller needs on an entity type, replacing KubeOps'
/// <c>EntityRbac</c>. Consumed by the CRD/RBAC code generator (no runtime effect).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EntityRbacAttribute(Type entityType) : Attribute
{
    public Type EntityType { get; } = entityType;

    public RbacVerb Verbs { get; set; } = RbacVerb.All;
}
