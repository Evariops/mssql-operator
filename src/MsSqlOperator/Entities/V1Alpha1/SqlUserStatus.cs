using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// Slim, etcd-light status: convergence signal + O(1) roll-up scalars only. Per-target /
/// per-replica login and per-database user/role detail is no longer stored here — it is served live by
/// the aggregated APIService (<c>sqlusertargets</c>), emitted as Events, and counted via metrics.
/// </summary>
public class SqlUserStatus
{
    public string Phase { get; set; } = "Pending";
    public long ObservedGeneration { get; set; }

    public string? EffectiveExpiresAt { get; set; }
    public bool Expired { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>Targets (instances/AGs) matched by the selector.</summary>
    public int TotalTargets { get; set; }

    /// <summary>Targets fully provisioned this pass.</summary>
    public int ReadyTargets { get; set; }

    /// <summary>Targets that failed provisioning this pass.</summary>
    public int FailedTargets { get; set; }

    public List<ConditionStatus>? Conditions { get; set; }
}
