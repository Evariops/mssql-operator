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
    int? ReplicaCount,
    string? MasterKeyPassword = null)
{
    // MasterKeyPassword: the password protecting the target's Database Master Key, when
    // it is NOT the SA password. An availability group creates its DMK with masterKeyPasswordSecretRef
    // (mandatory at admission), so opening it with the SA password — which is what the TDE controller
    // did — failed from the very first reconcile, and TDE on an AG never worked. On a SqlInstance the
    // DMK IS created with the SA password, and a routine SA rotation then broke every later OPEN
    // MASTER KEY. Null means "protected by the SA password" (the legacy SqlInstance case).

    /// <summary>The password to use for CREATE/OPEN MASTER KEY on this target.</summary>
    public string EffectiveMasterKeyPassword => MasterKeyPassword ?? SaPassword;

    // PrintMembers is the single rendering hook the record's ToString and any derived
    // record use — masking the SA password here covers every formatting path.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Name = {Name}, Namespace = {Namespace}, Host = {Host}, Port = {Port}, SaPassword = ***, ReplicaCount = {ReplicaCount}, MasterKeyPassword = ***");
        return true;
    }
}
