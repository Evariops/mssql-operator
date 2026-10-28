using System.Collections.Concurrent;

namespace MsSqlOperator.Operator.Runtime;

/// <summary>
/// Owners whose key-material Secret was deleted since their last slow pass. The
/// watch reconciled the owner at once, but the Secret is only rebuilt by the slow pass —
/// up to a minute later (37 s observed). The next pass of such an owner is a slow one.
/// </summary>
public static class InfrastructureDue
{
    private static readonly ConcurrentDictionary<string, byte> Owners = new();

    public static void Mark(string ownerUid) => Owners[ownerUid] = 0;

    /// <summary>True once per mark: the caller runs its slow pass now.</summary>
    public static bool Consume(string ownerUid) => Owners.TryRemove(ownerUid, out _);
}
