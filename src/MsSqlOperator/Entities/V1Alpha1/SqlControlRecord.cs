using System.ComponentModel.DataAnnotations;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// The control registry of one availability group (<c>ag-&lt;name&gt;</c>) or one
/// SqlInstance (<c>instance-&lt;name&gt;</c>) — the operator's epoch, the one operation in progress,
/// the open divergences, the removed replicas and the machine-key fingerprint. It carries no
/// ownerReference: it survives the deletion and the recreation of the resource (GitOps), and losing
/// the status of the resource no longer loses any safety state. Written by the operator only, at
/// transitions only; the legitimate primary is not in it (it is read in SQL), nor the routing (it
/// lives in the listener Service).
/// </summary>
[KubernetesEntity(Group = "mssql-operator.evariops.com", ApiVersion = "v1alpha1", Kind = "SqlControlRecord", PluralName = "sqlcontrolrecords")]
[PrinterColumn("Owner", ".spec.owner.name")]
[PrinterColumn("Epoch", ".spec.epoch", "integer")]
[PrinterColumn("Intent", ".spec.intent.type")]
[PrinterColumn("Stage", ".spec.intent.stage")]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlControlRecord : CustomKubernetesEntity<SqlControlRecordSpec>
{
    /// <summary>The name of the record of an availability group.</summary>
    public static string ForAg(string ag) => $"ag-{ag}";

    /// <summary>The name of the record of a SqlInstance.</summary>
    public static string ForInstance(string instance) => $"instance-{instance}";
}

[CelRule("self.epoch >= oldSelf.epoch", "a stale writer cannot lower the epoch of the control record")]
[CelRule("!has(oldSelf.intent) || !has(self.intent) || self.intent.id == oldSelf.intent.id || self.epoch > oldSelf.epoch",
    "an intent in progress is replaced only by a writer of a newer epoch")]
public class SqlControlRecordSpec
{
    /// <summary>The resource this record controls.</summary>
    public ControlOwner Owner { get; set; } = new();

    /// <summary>The epoch of the last writer: it never decreases (CEL).</summary>
    // Bounded like the int32 leaseTransitions of the epoch Lease — long.MaxValue, written
    // as a double, was an int64 bound the API server refused, and no record could be created.
    [Range(0, int.MaxValue)]
    public long Epoch { get; set; }

    /// <summary>The durability profile, fixed at the creation of the AG.</summary>
    public ControlDurability? Durability { get; set; }

    /// <summary>The one operation in progress; null outside an operation.</summary>
    public ControlIntent? Intent { get; set; }

    /// <summary>Open divergences after a data-loss failover; the detail lives in the databases.</summary>
    [MaxLength(16)]
    public List<ControlDivergence>? OpenDivergences { get; set; }

    /// <summary>Replicas removed from the AG by the operator, so that they are not added back blindly.</summary>
    [MaxLength(8)]
    public List<ControlRemovedReplica>? RemovedReplicas { get; set; }

    /// <summary>The fingerprint of the machine key the data was created with.</summary>
    public ControlKeys? Keys { get; set; }

    /// <summary>When the operator last ran a destructive action on its own, counted against its destructive budget.</summary>
    public DateTime? LastDestructiveAt { get; set; }

    /// <summary>When the record was rebuilt from SQL because it was missing (observation mode).</summary>
    public DateTime? ReconstructedAt { get; set; }
}

public class ControlOwner
{
    [MaxLength(63)]
    public string Kind { get; set; } = "";

    [MaxLength(253)]
    public string Name { get; set; } = "";

    [MaxLength(64)]
    public string? Uid { get; set; }
}

public class ControlDurability
{
    /// <summary>Quorum | Legacy.</summary>
    [RegularExpression("^(Quorum|Legacy)$")]
    public string Profile { get; set; } = "Legacy";

    [Range(0, 8)]
    public int RequiredSyncFloor { get; set; }

    public DateTime? FixedAt { get; set; }
}

public class ControlIntent
{
    [MaxLength(128)]
    public string Id { get; set; } = "";

    /// <summary>Failover | Switchover | ForceFailoverDataLoss | RestartPrimary | RemoveReplica | ReaddReplica.</summary>
    [RegularExpression("^(Failover|Switchover|ForceFailoverDataLoss|RestartPrimary|RemoveReplica|ReaddReplica)$")]
    public string Type { get; set; } = "";

    [MaxLength(253)]
    public string? From { get; set; }

    [MaxLength(253)]
    public string? To { get; set; }

    [MaxLength(64)]
    public string Stage { get; set; } = "";

    /// <summary>Replicas isolated by this election.</summary>
    [MaxLength(8)]
    public List<string>? Isolated { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? Deadline { get; set; }

    [Range(0, int.MaxValue)]
    public long Epoch { get; set; }

    [Range(0, 1000)]
    public int Attempts { get; set; }
}

public class ControlDivergence
{
    [MaxLength(128)]
    public string Id { get; set; } = "";

    [Range(0, int.MaxValue)]
    public long Epoch { get; set; }

    [MaxLength(253)]
    public string? From { get; set; }

    [MaxLength(253)]
    public string? To { get; set; }

    public DateTime? At { get; set; }
}

public class ControlRemovedReplica
{
    [MaxLength(253)]
    public string Replica { get; set; } = "";

    public DateTime? At { get; set; }

    [Range(0, int.MaxValue)]
    public long Epoch { get; set; }
}

public class ControlKeys
{
    [MaxLength(64)]
    public string? MachineKeySha256 { get; set; }

    public DateTime? RecordedAt { get; set; }
}
