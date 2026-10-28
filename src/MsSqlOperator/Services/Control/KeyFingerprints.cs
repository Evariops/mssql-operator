using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// The fingerprint of the machine key the data was created with lives in the control
/// record, which survives the deletion and the recreation of the resource. The status alone forgot
/// it with a recreated CR — and then took the new, wrong, key as the reference. While the key differs,
/// no voluntary restart is made: a restarted pod would load it, and its Service Master Key would no
/// longer decrypt.
/// </summary>
public sealed class KeyFingerprints(ControlRegistry registry, Operator.Registration.OperatorOptions options, ILogger<KeyFingerprints> logger)
{
    public const string MachineKeyChangedCondition = "MachineKeyChanged";

    private bool Enabled(IKubernetesObject<V1ObjectMeta> owner)
        => SafetyLevel.Mode(1, SafetyLevel.Effective(owner.Metadata.Annotations?.TryGetValue(SafetyLevel.Annotation, out var l) == true ? l : null,
            options.SafetyLevelMax), options.SafetyLevelMax) is not null;

    /// <summary>The fingerprint recorded in the control record, when level 1 runs; null otherwise.</summary>
    public async Task<string?> RecordedAsync(IKubernetesObject<V1ObjectMeta> owner, string record, CancellationToken ct)
    {
        if (!Enabled(owner))
            return null;
        try
        {
            return (await registry.GetAsync(record, owner.Metadata.NamespaceProperty, ct))?.Spec.Keys?.MachineKeySha256;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the machine key fingerprint of {Record}", record);
            return null;
        }
    }

    /// <summary>Records the fingerprint on first sight; never overwrites one.</summary>
    public async Task RecordAsync(IKubernetesObject<V1ObjectMeta> owner, string record, string sha256, DateTime now, CancellationToken ct)
    {
        if (!Enabled(owner))
            return;
        try
        {
            var existing = await registry.EnsureAsync(owner, record, null, ct);
            if (existing.Spec.Keys?.MachineKeySha256 is null)
                await registry.UpdateAsync(existing, spec => spec.Keys = new ControlKeys { MachineKeySha256 = sha256, RecordedAt = now }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not record the machine key fingerprint of {Record}", record);
        }
    }

    /// <summary>True while the mounted machine key is not the original one: no voluntary restart.</summary>
    public static bool BlocksRestarts(IEnumerable<ConditionStatus>? conditions)
        => conditions?.Any(c => c.Type == MachineKeyChangedCondition && c.Status == "True") == true;
}
