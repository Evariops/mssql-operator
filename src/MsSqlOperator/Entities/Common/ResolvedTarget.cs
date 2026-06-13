namespace MsSqlOperator.Entities.Common;

/// <summary>
/// A resolved connection target. NOTE: <see cref="SaPassword"/> is a secret — the
/// record's <see cref="ToString"/> is overridden so a log statement like <c>{target}</c> never
/// prints it. This type carries operational data, not API schema (it is never serialized into
/// a CRD); it stays out of the generated schema because no entity property references it.
/// </summary>
public record ResolvedTarget(
    string Kind,        // "SqlInstance" | "SqlAvailabilityGroup"
    string Name,
    string Namespace,
    string Host,
    int Port,
    string SaPassword,
    int? ReplicaCount)
{
    // PrintMembers is the single rendering hook the record's ToString and any derived
    // record use — masking the SA password here covers every formatting path.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Name = {Name}, Namespace = {Namespace}, Host = {Host}, Port = {Port}, SaPassword = ***, ReplicaCount = {ReplicaCount}");
        return true;
    }
}
