using System.Security.Cryptography;
using System.Text;

namespace MsSqlOperator.Services.Actuation;

/// <summary>What an action can cost. Safe: nothing lost, nothing interrupted. Disruptive:
/// interrupts (a failover, a fenced pod, a re-seed) but loses nothing. Destructive: loses something
/// that exists nowhere else, or cannot be undone.</summary>
public enum RiskClass { Safe, Disruptive, Destructive }

/// <summary>The typed actions of the operator that are not Safe.</summary>
public enum ActionType
{
    // Disruptive
    Failover,
    DemoteReplica,
    RemoveReplica,
    DropLocalAvailabilityGroup,
    DropSecondaryCopy,
    KillSessions,
    DisableLogin,
    DeletePod,
    ForceDeletePod,
    DeleteVolumeAttachment,
    TaintNode,
    RaiseAvailabilityMode,
    DropOrphanCopy,
    // Destructive
    ForceFailoverDataLoss,
    ResumeDivergentReplica,
    RemoveDatabaseFromAg,
    DropLogin,
    DropDatabaseUser,
    DropProcedure,
    DeleteJob,
    DropDatabase,
    GenerateMachineKey,
    RegenerateServiceMasterKey,
}

public static class ActionCatalog
{
    public static RiskClass Risk(ActionType type) => type switch
    {
        ActionType.ForceFailoverDataLoss or ActionType.ResumeDivergentReplica or ActionType.RemoveDatabaseFromAg
            or ActionType.DropLogin or ActionType.DropDatabaseUser or ActionType.DropProcedure or ActionType.DeleteJob or ActionType.DropDatabase
            or ActionType.GenerateMachineKey or ActionType.RegenerateServiceMasterKey => RiskClass.Destructive,
        _ => RiskClass.Disruptive,
    };

    /// <summary>Never automatic, whatever the mode or the pre-authorizations: what they protect is
    /// lost for good.</summary>
    public static bool AlwaysDenied(ActionType type)
        => type is ActionType.RegenerateServiceMasterKey or ActionType.GenerateMachineKey;
}

/// <summary>Why the action is wanted, besides the operator's own decision: the deletion of the
/// resource (a finalizer's cleanup), what a human declared in the spec (a database removed from a
/// user, DropAndCreate, allowAutomaticDataLossFailover), or an approval a human gave in an
/// annotation the controller reads itself (force-failover with allow-data-loss, approve-dataloss-resume).</summary>
public enum ActuationIntent { Operator, ResourceDeletion, SpecDeclared, HumanAnnotation }

/// <summary>
/// Marks a manager method whose effect cannot be taken back without cost (Disruptive or
/// Destructive). It is called from Services/Actuation only, through the gateway: a decision of
/// policy, traced, with its preconditions checked again right before it runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DestructiveAttribute(ActionType action) : Attribute
{
    public ActionType Action { get; } = action;
}

/// <summary>
/// An action and its deterministic identifier, <c>&lt;type&gt;:&lt;target&gt;:&lt;epoch&gt;:&lt;fingerprint&gt;</c>
/// — the one an approval names, the one the event and the plan show.
/// </summary>
public sealed record ActionRequest(ActionType Type, string Target, string Epoch, string Description, params string[] Parameters)
{
    public RiskClass Risk => ActionCatalog.Risk(Type);

    public string Id => $"{Type}:{Target}:{Epoch}:{Fingerprint}";

    public string Fingerprint
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', Parameters))))[..8];
}

/// <summary>What the policy decided.</summary>
public enum DecisionKind { Auto, Approved, RequiresApproval, Withheld, Denied }

public sealed record PolicyDecision(DecisionKind Kind, string Reason)
{
    public bool Proceeds => Kind is DecisionKind.Auto or DecisionKind.Approved;
}

/// <summary>An action the gateway did not run; the caller treats it as not done (the finalizer
/// keeps its finalizer, the revocation stays pending, the re-seed waits).</summary>
public sealed class ActionWithheldException(ActionRequest request, PolicyDecision decision)
    : InvalidOperationException($"{request.Id} not run: {decision.Kind} — {decision.Reason}")
{
    public ActionRequest Request { get; } = request;
    public PolicyDecision Decision { get; } = decision;
}
