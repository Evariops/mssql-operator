using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Controllers;

/// <summary>
/// SqlDatabase: the database exists on its server (created, or adopted when it was already there),
/// with the options of the spec. On an availability group it is created on the primary and joined
/// through a SqlAvailabilityGroupDatabase this controller manages.
///
/// Ownership lives in the database itself (extended property <c>mssqlop.owner</c>): it follows the
/// database through a failover, and a resource re-created by GitOps (new UID) takes its database back
/// once the former owner is gone. A database owned by another live SqlDatabase is never touched.
/// </summary>
[EntityRbac(typeof(SqlDatabase), Verbs = RbacVerb.All)]
[EntityRbac(typeof(SqlAvailabilityGroupDatabase), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
public class SqlDatabaseController(
    ILogger<SqlDatabaseController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlDatabaseManager databases,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    TimeProvider? timeProvider = null,
    TargetLimiter? targetLimiter = null) : IEntityController<SqlDatabase>
{
    /// <summary>On the SqlAvailabilityGroupDatabase a SqlDatabase manages: the SqlDatabase's name and UID.</summary>
    public const string ManagedByAnnotation = "mssql-operator.evariops.com/sqldatabase";
    public const string ManagedByUidAnnotation = "mssql-operator.evariops.com/sqldatabase-uid";

    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    internal static string? AnnotationOf(V1ObjectMeta metadata, string key)
        => metadata.Annotations is { } annotations && annotations.TryGetValue(key, out var value) ? value : null;

    /// <summary>The value of the owner property for this resource.</summary>
    public static string OwnerTag(SqlDatabase entity)
        => $"SqlDatabase/{entity.Metadata.NamespaceProperty}/{entity.Metadata.Name}/{entity.Metadata.Uid}";

    public async Task<ReconciliationResult<SqlDatabase>> ReconcileAsync(SqlDatabase entity, CancellationToken ct)
    {
        entity.Status ??= new SqlDatabaseStatus();
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        var spec = entity.Spec;
        var ns = entity.Metadata.NamespaceProperty;
        var onAg = spec.AvailabilityGroupRef is { Name.Length: > 0 };
        var targetName = onAg ? spec.AvailabilityGroupRef!.Name : spec.InstanceRef?.Name ?? "";

        var target = (await targetResolver.ResolveAsync(onAg
                ? new TargetSelector { AvailabilityGroups = [targetName] }
                : new TargetSelector { Instances = [targetName] }, ns, ct))
            .FirstOrDefault();
        if (target is null || !await TargetReadyAsync(onAg, targetName, ns, ct))
            return await FinishAsync(entity, Phases.Pending, "False", "WaitingForTarget",
                $"{(onAg ? "SqlAvailabilityGroup" : "SqlInstance")} '{targetName}' is not running yet", ct, WaitInterval);

        await using var slot = await targetLimiter.ForTargetAsync(target.Kind, target.Namespace, target.Name, ct);
        SqlConnection connection;
        string server;
        if (onAg)
        {
            if (await targetResolver.FindPrimaryReplicaAsync(target.Name, ns, target.SaPassword, target.ReplicaCount ?? 3, ct) is not { } primary)
                return await FinishAsync(entity, Phases.Pending, "False", "NoPrimary",
                    $"Availability group '{targetName}' has no primary to create the database on", ct, WaitInterval);
            (server, connection) = (primary.ReplicaName, primary.Connection);
        }
        else
        {
            connection = await connectionFactory.OpenAsync(target.Host, target.Port, target.SaPassword, SqlProfile.Ddl, "master", ct);
            server = target.Name;
        }

        await using (connection)
        {
            entity.Status.Server = server;
            var database = await databases.GetDatabaseAsync(connection, spec.DatabaseName, ct);
            if (database is null)
            {
                if (await CreateAsync(entity, connection, target, server, ct) is { } refused)
                    return refused;
                database = await databases.GetDatabaseAsync(connection, spec.DatabaseName, ct)
                           ?? throw new InvalidOperationException($"Database '{spec.DatabaseName}' was created on {server} but cannot be read back.");
            }
            else if (!database.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                return await FinishAsync(entity, Phases.Pending, "False", "DatabaseNotOnline",
                    $"Database '{spec.DatabaseName}' on {server} is {database.State}", ct, WaitInterval);
            }
            else if (await ClaimAsync(entity, connection, server, ct) is { } conflict)
            {
                return conflict;
            }

            await ConvergeOptionsAsync(entity, connection, database, target, server, ct);
            database = await databases.GetDatabaseAsync(connection, spec.DatabaseName, ct) ?? database;
            entity.Status.Collation = database.Collation;
            entity.Status.RecoveryModel = database.RecoveryModel;
            entity.Status.CompatibilityLevel = database.CompatibilityLevel;
            entity.Status.Owner = database.Owner;
            SetCondition(entity, "CollationMismatch", spec.Collation is { } c && !c.Equals(database.Collation, StringComparison.OrdinalIgnoreCase),
                "CollationDiffers", $"The database uses {database.Collation}, not {spec.Collation}: a collation cannot change in place.");
        }

        if (!onAg)
            return await FinishAsync(entity, Phases.Ready, "True", "DatabaseReady", $"Database '{spec.DatabaseName}' is ready on {server}", ct);
        return await JoinAvailabilityGroupAsync(entity, targetName, ct);
    }

    public Task<ReconciliationResult<SqlDatabase>> DeletedAsync(SqlDatabase entity, CancellationToken ct)
        => Task.FromResult(ReconciliationResult<SqlDatabase>.Success(entity));

    private async Task<bool> TargetReadyAsync(bool onAg, string name, string ns, CancellationToken ct)
    {
        if (onAg)
            return await kubernetesClient.GetAsync<SqlAvailabilityGroup>(name, ns, ct) is { Status: { PrimaryReplica.Length: > 0 } status }
                   && status.Phase is Phases.Running or Phases.Degraded;
        return await kubernetesClient.GetAsync<SqlInstance>(name, ns, ct) is { Status.Phase: Phases.Running };
    }

    /// <summary>Creates the database and records its owner; a refusal result when it cannot.</summary>
    private async Task<ReconciliationResult<SqlDatabase>?> CreateAsync(SqlDatabase entity, SqlConnection connection, ResolvedTarget target,
        string server, CancellationToken ct)
    {
        var spec = entity.Spec;
        if (spec.Collation is { } collation && !await databases.CollationExistsAsync(connection, collation, ct))
            return await FinishAsync(entity, Phases.Failed, "False", "UnknownCollation",
                $"Collation '{collation}' does not exist on {server}", ct, RetryInterval);
        if (await VolumeShortageAsync(target, spec.Files, ct) is { } shortage)
            return await FinishAsync(entity, Phases.Failed, "False", "InsufficientSpace", shortage, ct, RetryInterval);

        entity.Status!.Phase = Phases.Creating;
        await databases.CreateDatabaseAsync(connection, spec.DatabaseName, spec.Collation, ct);
        // Written right after the creation: a crash in between leaves an unowned database, which the
        // next pass adopts — the same outcome.
        await databases.SetOwnerTagAsync(connection, spec.DatabaseName, OwnerTag(entity), ct);
        entity.Status.Adopted = false;
        entity.Status.CreatedAt = _clock.GetUtcNow().UtcDateTime;
        await eventPublisher.PublishAsync(entity, "DatabaseCreated", $"Database '{spec.DatabaseName}' created on {server}", ct: ct);
        logger.LogInformation("Created database {Db} on {Server}", spec.DatabaseName, server);
        return null;
    }

    /// <summary>Takes the database when nobody owns it (adoption), or when its owner is a former
    /// incarnation of this resource; a Conflict result when another live SqlDatabase owns it.</summary>
    private async Task<ReconciliationResult<SqlDatabase>?> ClaimAsync(SqlDatabase entity, SqlConnection connection, string server,
        CancellationToken ct)
    {
        var dbName = entity.Spec.DatabaseName;
        var mine = OwnerTag(entity);
        var tag = await databases.GetOwnerTagAsync(connection, dbName, ct);
        if (tag == mine)
            return null;

        if (tag is null)
        {
            await databases.SetOwnerTagAsync(connection, dbName, mine, ct);
            entity.Status!.Adopted = true;
            entity.Status.CreatedAt = _clock.GetUtcNow().UtcDateTime;
            await eventPublisher.PublishAsync(entity, "DatabaseAdopted",
                $"Database '{dbName}' already existed on {server}: it is now managed by this SqlDatabase", ct: ct);
            return null;
        }

        if (await OwnerStillExistsAsync(tag, ct))
        {
            var message = $"Database '{dbName}' on {server} is owned by {tag}: two SqlDatabases cannot manage one database. Nothing was changed.";
            await eventPublisher.PublishAsync(entity, "DatabaseOwnedByAnotherResource", message, "Warning", ct);
            return await FinishAsync(entity, Phases.Conflict, "False", "DatabaseOwnedByAnotherResource", message, ct, TimeSpan.FromMinutes(5));
        }

        await databases.SetOwnerTagAsync(connection, dbName, mine, ct);
        entity.Status!.Adopted = true;
        await eventPublisher.PublishAsync(entity, "DatabaseOwnershipTakenOver",
            $"Database '{dbName}' on {server} belonged to {tag}, which no longer exists: it is now managed by this SqlDatabase", ct: ct);
        return null;
    }

    private async Task<bool> OwnerStillExistsAsync(string tag, CancellationToken ct)
    {
        var parts = tag.Split('/');
        if (parts.Length != 4 || parts[0] != "SqlDatabase")
            return true; // not ours to judge: kept
        var current = await kubernetesClient.GetAsync<SqlDatabase>(parts[2], parts[1], ct);
        return current?.Metadata.Uid == parts[3];
    }

    private async Task ConvergeOptionsAsync(SqlDatabase entity, SqlConnection connection, DatabaseInfo database, ResolvedTarget target,
        string server, CancellationToken ct)
    {
        var spec = entity.Spec;
        var dbName = spec.DatabaseName;

        if (spec.RecoveryModel is { } model && !SqlName(model).Equals(database.RecoveryModel, StringComparison.OrdinalIgnoreCase))
        {
            await databases.SetRecoveryModelAsync(connection, dbName, SqlName(model), ct);
            await eventPublisher.PublishAsync(entity, "RecoveryModelChanged", $"'{dbName}': recovery model {database.RecoveryModel} → {SqlName(model)}", ct: ct);
        }

        if (spec.CompatibilityLevel is { } level && level != database.CompatibilityLevel)
        {
            await databases.SetCompatibilityLevelAsync(connection, dbName, level, ct);
            await eventPublisher.PublishAsync(entity, "CompatibilityLevelChanged", $"'{dbName}': compatibility level {database.CompatibilityLevel} → {level}", ct: ct);
        }

        var ownerMissing = false;
        if (spec.Owner is { } owner && !owner.Equals(database.Owner, StringComparison.OrdinalIgnoreCase))
        {
            if (await databases.LoginExistsAsync(connection, owner, ct))
            {
                await databases.SetOwnerAsync(connection, dbName, owner, ct);
                await eventPublisher.PublishAsync(entity, "DatabaseOwnerChanged", $"'{dbName}': owner {database.Owner} → {owner}", ct: ct);
            }
            else
            {
                ownerMissing = true;
            }
        }
        SetCondition(entity, "OwnerLoginMissing", ownerMissing, "LoginNotFound",
            $"Login '{spec.Owner}' does not exist on {server} yet: the owner is applied once it does (a SqlUser creates it).");

        if (spec.Files is { } files)
        {
            var current = await databases.GetFilesAsync(connection, dbName, ct);
            var grows = new List<(DatabaseFileInfo File, long? Size)>();
            foreach (var (desired, type) in new[] { (files.Data, 0), (files.Log, 1) })
            {
                if (desired is null || current.FirstOrDefault(f => f.Type == type) is not { } file)
                    continue;
                var size = desired.Size is { } s && SqlDatabaseValidator.Bytes(s) is { } bytes && bytes > file.SizeBytes ? bytes : (long?)null;
                var (growth, percent) = Growth(desired.Growth);
                var growthChange = growth is { } g && (g != file.Growth || percent != file.GrowthIsPercent) ? growth : null;
                var max = MaxSize(desired.MaxSize);
                var maxChange = max is { } m && m != file.MaxSizeBytes ? max : null;
                if (size is null && growthChange is null && maxChange is null)
                    continue;
                if (size is not null)
                    grows.Add((file, size));
                await databases.ModifyFileAsync(connection, dbName, file.LogicalName, size, growthChange, percent, maxChange, ct);
            }
            if (grows.Count > 0)
                await eventPublisher.PublishAsync(entity, "DatabaseFilesGrown",
                    $"'{dbName}': {string.Join(", ", grows.Select(g => $"{g.File.LogicalName} → {g.Size / 1048576} MB"))}", ct: ct);
        }
    }

    /// <summary>
    /// The SqlAvailabilityGroupDatabase that joins the database to its AG: created when missing,
    /// adopted when one already joins this database, kept in line with spec.availabilityGroup (the
    /// blocks set there only).
    /// </summary>
    private async Task<ReconciliationResult<SqlDatabase>> JoinAvailabilityGroupAsync(SqlDatabase entity, string agName, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var spec = entity.Spec;
        var existing = (await kubernetesClient.ListAsync<SqlAvailabilityGroupDatabase>(ns, null, ct))
            .FirstOrDefault(d => d.Spec.AvailabilityGroupRef.Name == agName
                                 && d.Spec.DatabaseName.Equals(spec.DatabaseName, StringComparison.OrdinalIgnoreCase));

        SqlAvailabilityGroupDatabase agd;
        if (existing is null)
        {
            if (await kubernetesClient.GetAsync<SqlAvailabilityGroupDatabase>(entity.Metadata.Name, ns, ct) is not null)
                return await FinishAsync(entity, Phases.Conflict, "False", "AvailabilityGroupDatabaseNameTaken",
                    $"A SqlAvailabilityGroupDatabase named '{entity.Metadata.Name}' already exists for another database", ct, TimeSpan.FromMinutes(5));
            agd = new SqlAvailabilityGroupDatabase
            {
                ApiVersion = "mssql-operator.evariops.com/v1alpha1",
                Kind = "SqlAvailabilityGroupDatabase",
                Metadata = new V1ObjectMeta
                {
                    Name = entity.Metadata.Name,
                    NamespaceProperty = ns,
                    Annotations = new Dictionary<string, string>
                    {
                        [ManagedByAnnotation] = entity.Metadata.Name,
                        [ManagedByUidAnnotation] = entity.Metadata.Uid ?? "",
                    },
                },
                Spec = new SqlAvailabilityGroupDatabaseSpec { AvailabilityGroupRef = new() { Name = agName }, DatabaseName = spec.DatabaseName },
            };
            ApplyAgSettings(entity, agd.Spec);
            agd = await kubernetesClient.CreateAsync(agd, ct);
            await eventPublisher.PublishAsync(entity, "AvailabilityGroupDatabaseCreated",
                $"SqlAvailabilityGroupDatabase '{agd.Metadata.Name}' created to join '{spec.DatabaseName}' to '{agName}'", ct: ct);
        }
        else
        {
            agd = existing;
            var managedBy = AnnotationOf(agd.Metadata, ManagedByAnnotation);
            var managedUid = AnnotationOf(agd.Metadata, ManagedByUidAnnotation);
            if (managedUid != entity.Metadata.Uid)
            {
                if (managedBy is not null && managedUid is not null
                    && (await kubernetesClient.GetAsync<SqlDatabase>(managedBy, ns, ct))?.Metadata.Uid == managedUid)
                    return await FinishAsync(entity, Phases.Conflict, "False", "AvailabilityGroupDatabaseManagedElsewhere",
                        $"SqlAvailabilityGroupDatabase '{agd.Metadata.Name}' is managed by SqlDatabase '{managedBy}'", ct, TimeSpan.FromMinutes(5));
                agd = await kubernetesClient.PatchMetadataAsync(agd, new Dictionary<string, string?>
                {
                    [ManagedByAnnotation] = entity.Metadata.Name,
                    [ManagedByUidAnnotation] = entity.Metadata.Uid,
                }, ct);
                await eventPublisher.PublishAsync(entity, "AvailabilityGroupDatabaseAdopted",
                    $"SqlAvailabilityGroupDatabase '{agd.Metadata.Name}' already joins '{spec.DatabaseName}' to '{agName}': it is now managed by this SqlDatabase", ct: ct);
            }
            // Custom resources are patched, never PUT: one operation per block that differs.
            var desired = k8s.KubernetesJson.Deserialize<SqlAvailabilityGroupDatabaseSpec>(k8s.KubernetesJson.Serialize(agd.Spec));
            ApplyAgSettings(entity, desired);
            var operations = new List<Dictionary<string, object?>>();
            foreach (var (path, current, wanted) in new (string, object?, object?)[]
                     {
                         ("/spec/seedingMode", agd.Spec.SeedingMode, desired.SeedingMode),
                         ("/spec/logBackup", agd.Spec.LogBackup, desired.LogBackup),
                         ("/spec/initialization", agd.Spec.Initialization, desired.Initialization),
                         ("/spec/dataMovement", agd.Spec.DataMovement, desired.DataMovement),
                         ("/spec/automation", agd.Spec.Automation, desired.Automation),
                     })
                if (Json(current) != Json(wanted))
                    operations.Add(MsSqlOperator.Operator.Client.JsonPatch.Add(path, wanted));
            if (operations.Count > 0)
                agd = await kubernetesClient.PatchAsync(agd, operations, ct);
        }

        entity.Status!.AvailabilityGroupDatabase = agd.Metadata.Name;
        entity.Status.AvailabilityGroupPhase = agd.Status?.Phase;
        return agd.Status?.Phase == Phases.Synchronized
            ? await FinishAsync(entity, Phases.Ready, "True", "DatabaseReady",
                $"Database '{spec.DatabaseName}' is synchronized in availability group '{agName}'", ct)
            : await FinishAsync(entity, Phases.Joining, "False", "JoiningAvailabilityGroup",
                $"Database '{spec.DatabaseName}' is joining availability group '{agName}' ({agd.Status?.Phase ?? Phases.Pending})", ct, WaitInterval);
    }

    /// <summary>The blocks of spec.availabilityGroup that are set, and the automation of the resource.</summary>
    private static void ApplyAgSettings(SqlDatabase entity, SqlAvailabilityGroupDatabaseSpec target)
    {
        var ag = entity.Spec.AvailabilityGroup;
        if (ag?.SeedingMode is { } seeding) target.SeedingMode = seeding;
        if (ag?.LogBackup is { } logBackup) target.LogBackup = logBackup;
        if (ag?.Initialization is { } initialization) target.Initialization = initialization;
        if (ag?.DataMovement is { } dataMovement) target.DataMovement = dataMovement;
        if (entity.Spec.Automation is { } automation) target.Automation = automation;
    }

    /// <summary>A declared size larger than the volume it goes to: the message, or null. The volume
    /// of every replica must hold it — the secondaries receive the same files.</summary>
    private async Task<string?> VolumeShortageAsync(ResolvedTarget target, SqlDatabaseFilesSpec? files, CancellationToken ct)
    {
        if (files is null)
            return null;
        var pods = target.IsAvailabilityGroup
            ? Enumerable.Range(0, target.ReplicaCount ?? 3).Select(i => $"{target.Name}-{i}")
            : [$"{target.Name}-0"];
        foreach (var (file, volume) in new[] { (files.Data, "data"), (files.Log, "log") })
        {
            if (file?.Size is not { } size || SqlDatabaseValidator.Bytes(size) is not { } bytes)
                continue;
            foreach (var pod in pods)
                if (await kubernetesClient.GetAsync<V1PersistentVolumeClaim>($"{volume}-{pod}", target.Namespace, ct) is { Status.Capacity: { } capacity }
                    && capacity.TryGetValue("storage", out var quantity) && (long)quantity.ToDecimal() < bytes)
                    return $"The {volume} file ({size}) does not fit on the {volume} volume of {pod} ({quantity}): grow the volume first.";
        }
        return null;
    }

    private static string Json(object? value) => value is null ? "null" : k8s.KubernetesJson.Serialize(value);

    private static string SqlName(string recoveryModel) => recoveryModel.Equals("BulkLogged", StringComparison.OrdinalIgnoreCase)
        ? "BULK_LOGGED" : recoveryModel.ToUpperInvariant();

    private static (long? Value, bool Percent) Growth(string? growth)
    {
        if (growth is null)
            return (null, false);
        if (growth.EndsWith('%'))
            return (long.Parse(growth[..^1], System.Globalization.CultureInfo.InvariantCulture), true);
        // SQL Server keeps growth in 8 KB pages.
        return (SqlDatabaseValidator.Bytes(growth) is { } bytes ? (bytes + 8191) / 8192 * 8192 : null, false);
    }

    private static long? MaxSize(string? maxSize)
        => maxSize is null ? null
            : maxSize.Equals("Unlimited", StringComparison.OrdinalIgnoreCase) ? -1
            : SqlDatabaseValidator.Bytes(maxSize) is { } bytes ? (bytes + 8191) / 8192 * 8192 : null;

    private void SetCondition(SqlDatabase entity, string type, bool active, string reason, string message)
    {
        var conditions = entity.Status!.Conditions ??= [];
        if (active)
            ConditionOps.Set(conditions, type, "True", reason, message, now: _clock.GetUtcNow().UtcDateTime);
        else if (conditions.Any(c => c.Type == type && c.Status == "True"))
            ConditionOps.Set(conditions, type, "False", "Resolved", "", now: _clock.GetUtcNow().UtcDateTime);
    }

    private async Task<ReconciliationResult<SqlDatabase>> FinishAsync(SqlDatabase entity, string phase, string ready, string reason,
        string message, CancellationToken ct, TimeSpan? requeue = null)
    {
        entity.Status!.Phase = phase;
        ConditionOps.Set(entity.Status.Conditions ??= [], "Ready", ready, reason, message, now: _clock.GetUtcNow().UtcDateTime);
        var updated = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return ReconciliationResult<SqlDatabase>.Success(updated, requeue);
    }
}
