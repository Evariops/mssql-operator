using k8s;
using k8s.Models;
using Microsoft.Data.SqlClient;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Actuation;

/// <summary>
/// The typed actions. Every <see cref="DestructiveAttribute"/> method of a manager is called
/// from here, through <see cref="ActuationGateway.RunAsync"/>, and nowhere else (architecture test).
/// </summary>
public static class Actuators
{
    private static ActionRequest Request(IKubernetesObject<V1ObjectMeta> owner, ActionType type, string target, string description,
        params string[] parameters)
        => new(type, target, ActuationGateway.Epoch(owner), description, parameters);

    // ── Availability groups ────────────────────────────────────────────────

    public static Task DropSecondaryCopyAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection replicaConnection, string database, string replica, CancellationToken ct, string? agScope = null)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropSecondaryCopy, replica,
                $"drop the copy of {database} on {replica} so that it is re-seeded from the primary", database, replica),
            gateway.Stamped(owner, replicaConnection, agScope ?? owner.Metadata.Name, c => ag.DropSecondaryCopyAsync(replicaConnection, database, c)), ct);

    public static Task DropLocalAvailabilityGroupAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection replicaConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropLocalAvailabilityGroup, replica,
                $"drop the local definition of {agName} on {replica} before it joins again", agName, replica),
            gateway.Stamped(owner, replicaConnection, agName, c => ag.DropLocalAvailabilityGroupAsync(replicaConnection, agName, c)), ct);

    public static Task RemoveReplicaAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.RemoveReplica, replica, $"remove {replica} from {agName}", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.RemoveReplicaAsync(primaryConnection, agName, replica, c)), ct);

    public static Task RemoveDatabaseFromAgAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string database, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.RemoveDatabaseFromAg, database,
                $"remove {database} from {agName}", agName, database),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.RemoveDatabaseFromAgAsync(primaryConnection, agName, database, c)), ct,
            ActuationIntent.ResourceDeletion);

    /// <summary>The promotion of <paramref name="target"/>: lossless (Disruptive), or forced with
    /// possible data loss (Destructive) — approved by the annotation of a human, or declared by
    /// allowAutomaticDataLossFailover.</summary>
    public static Task PromoteAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection targetConnection, string agName, string target, bool allowDataLoss, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, PromotionRequest(owner, agName, target, allowDataLoss),
            gateway.Stamped(owner, targetConnection, agName, allowDataLoss
                ? c => ag.ForceFailoverWithDataLossAsync(targetConnection, agName, c)
                : c => ag.FailoverAsync(targetConnection, agName, c)), ct, intent);

    /// <summary>The request <see cref="PromoteAsync"/> submits: decided up front by a failover that
    /// would otherwise demote or fence the previous primary first.</summary>
    public static ActionRequest PromotionRequest(IKubernetesObject<V1ObjectMeta> owner, string agName, string target, bool allowDataLoss)
        => allowDataLoss
            ? Request(owner, ActionType.ForceFailoverDataLoss, target, $"force the failover of {agName} to {target}, accepting data loss", agName, target)
            : Request(owner, ActionType.Failover, target, $"fail {agName} over to {target}", agName, target);

    /// <summary><paramref name="replica"/> made synchronous-commit, on the way to Quorum.</summary>
    public static Task RaiseAvailabilityModeAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.RaiseAvailabilityMode, replica,
                $"make {replica} a synchronous-commit replica of {agName} (durability Quorum)", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.SetSynchronousCommitAsync(primaryConnection, agName, replica, c)), ct);

    /// <summary><paramref name="replica"/> made asynchronous-commit before a node maintenance
    /// stops it, so that the primary does not wait for it until the session timeout.</summary>
    public static Task LowerAvailabilityModeAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.LowerAvailabilityMode, replica,
                $"make {replica} an asynchronous-commit replica of {agName} before a node maintenance stops it", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.SetAsynchronousCommitAsync(primaryConnection, agName, replica, c)), ct);

    /// <summary><paramref name="replica"/> made synchronous-commit while the last synchronized
    /// synchronous secondary is away for a node maintenance.</summary>
    public static Task ReinforceAvailabilityModeAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.RaiseAvailabilityMode, replica,
                $"make {replica} a synchronous-commit replica of {agName} while a node maintenance takes its synchronous secondary", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.SetSynchronousCommitAsync(primaryConnection, agName, replica, c)), ct);

    /// <summary>The reinforcement of <paramref name="replica"/> ended: asynchronous-commit again.</summary>
    public static Task EndReinforcementAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.LowerAvailabilityMode, replica,
                $"make {replica} an asynchronous-commit replica of {agName} again, the node maintenance being over", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.SetAsynchronousCommitAsync(primaryConnection, agName, replica, c)), ct);

    /// <summary><paramref name="replica"/>, back from a node maintenance, made synchronous-commit again.</summary>
    public static Task RestoreAvailabilityModeAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgManager ag,
        SqlConnection primaryConnection, string agName, string replica, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.RaiseAvailabilityMode, replica,
                $"make {replica} a synchronous-commit replica of {agName} again, back from a node maintenance", agName, replica),
            gateway.Stamped(owner, primaryConnection, agName, c => ag.SetSynchronousCommitAsync(primaryConnection, agName, replica, c)), ct);

    // ── Logins and users ───────────────────────────────────────────────────

    public static Task DisableLoginAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlUserManager users,
        SqlConnection connection, string login, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DisableLogin, server, $"disable login {login} on {server}", login, server),
            c => users.DisableLoginAsync(connection, login, c), ct, intent);

    public static Task KillSessionsAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlUserManager users,
        SqlConnection connection, string login, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.KillSessions, server, $"kill the sessions of {login} on {server}", login, server),
            c => users.KillSessionsAsync(connection, login, c), ct, intent);

    public static Task DropDatabaseUserAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlUserManager users,
        SqlConnection connection, string database, string user, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropDatabaseUser, server,
                $"drop user {user} from database {database} on {server}", database, user, server),
            c => users.DropDatabaseUserAsync(connection, database, user, c), ct, intent);

    public static Task DropLoginAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlUserManager users,
        SqlConnection connection, string login, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropLogin, server, $"drop login {login} on {server}", login, server),
            c => users.DropLoginAsync(connection, login, c), ct, intent);

    // ── Procedures and jobs ────────────────────────────────────────────────

    public static Task DropProcedureAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlStoredProcedureManager procs,
        SqlConnection connection, string database, string schema, string name, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropProcedure, database, $"drop procedure {schema}.{name} in {database}",
                database, schema, name),
            c => procs.DropProcedureAsync(connection, database, schema, name, c), ct, intent);

    /// <summary>SqlDatabase: a copy it created on a primary that lost its role before the join,
    /// backed up then dropped — its content is kept in the backup file. The path of the backup.</summary>
    public static async Task<string> DropOrphanCopyAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner,
        ISqlDatabaseManager databases, SqlConnection connection, string database, string folder, string fileName, string server, CancellationToken ct)
    {
        var path = "";
        await gateway.RunAsync(owner, Request(owner, ActionType.DropOrphanCopy, server, $"back up then drop the orphan copy of {database} on {server}",
                database, server),
            async c => path = await databases.DropOrphanCopyAsync(connection, database, folder, fileName, c), ct);
        return path;
    }

    /// <summary>SqlDatabase with <c>deletionPolicy: Delete</c>: the database (or the copy a former
    /// secondary keeps RESTORING) on <paramref name="server"/>.</summary>
    public static Task DropDatabaseAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlDatabaseManager databases,
        SqlConnection connection, string database, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DropDatabase, server, $"drop database {database} on {server}", database, server),
            c => databases.DropDatabaseAsync(connection, database, c), ct, intent);

    public static Task DeleteJobAsync(this ActuationGateway gateway, IKubernetesObject<V1ObjectMeta> owner, ISqlAgentManager agent,
        SqlConnection connection, string jobName, string server, ActuationIntent intent, CancellationToken ct)
        => gateway.RunAsync(owner, Request(owner, ActionType.DeleteJob, server, $"delete job {jobName} on {server}", jobName, server),
            c => agent.DeleteJobAsync(connection, jobName, c), ct, intent);

    // ── Pods, storage ──────────────────────────────────────────────────────

    public static Task DeletePodAsync(this ActuationGateway gateway, V1Pod pod, IKubernetesClient client, int gracePeriodSeconds, CancellationToken ct)
        => gateway.RunAsync(pod, Request(pod, ActionType.DeletePod, pod.Metadata.Name,
                $"delete pod {pod.Metadata.Name} (grace {gracePeriodSeconds}s)", pod.Metadata.Uid ?? pod.Metadata.Name),
            c => client.DeletePodAsync(pod.Metadata.Name, pod.Metadata.NamespaceProperty, gracePeriodSeconds, c), ct);

    public static Task ForceDeletePodAsync(this ActuationGateway gateway, V1Pod pod, IKubernetesClient client, CancellationToken ct)
        => gateway.RunAsync(pod, Request(pod, ActionType.ForceDeletePod, pod.Metadata.Name,
                $"force-delete pod {pod.Metadata.Name}", pod.Metadata.Uid ?? pod.Metadata.Name),
            c => client.ForceDeletePodAsync(pod.Metadata.Name, pod.Metadata.NamespaceProperty, c), ct);

    public static Task ForceDeletePodAsync(this ActuationGateway gateway, V1Pod pod, IFencingDriver driver, CancellationToken ct)
        => gateway.RunAsync(pod, Request(pod, ActionType.ForceDeletePod, pod.Metadata.Name,
                $"force-delete pod {pod.Metadata.Name}", pod.Metadata.Uid ?? pod.Metadata.Name),
            c => driver.ForceDeletePodAsync(pod, c), ct);

    public static Task DetachVolumesAsync(this ActuationGateway gateway, V1Pod pod, IFencingDriver driver,
        IReadOnlyList<V1VolumeAttachment> attachments, CancellationToken ct)
        => gateway.RunAsync(pod, Request(pod, ActionType.DeleteVolumeAttachment, pod.Metadata.Name,
                $"detach {attachments.Count} volume(s) of pod {pod.Metadata.Name}", [.. attachments.Select(a => a.Metadata.Name)]),
            c => driver.DetachVolumesAsync(pod, attachments, c), ct);

    public static Task DeleteVolumeAttachmentAsync(this ActuationGateway gateway, V1Pod pod, IKubernetesClient client,
        V1VolumeAttachment attachment, CancellationToken ct)
        => gateway.RunAsync(pod, Request(pod, ActionType.DeleteVolumeAttachment, pod.Metadata.Name,
                $"delete VolumeAttachment {attachment.Metadata.Name} of pod {pod.Metadata.Name}", attachment.Metadata.Name),
            c => client.DeleteAsync(attachment, c), ct);
}
