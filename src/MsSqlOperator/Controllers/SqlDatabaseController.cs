using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Actuation;
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
    TargetLimiter? targetLimiter = null,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null) : IEntityController<SqlDatabase>
{
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

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
        var ownerPending = false;
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
                if (onAg && await StrayCopiesAsync(entity, target, server, ct) is { } stray)
                    return stray;
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

            ownerPending = await ConvergeOptionsAsync(entity, connection, database, target, server, ct);
            database = await databases.GetDatabaseAsync(connection, spec.DatabaseName, ct) ?? database;
            entity.Status.Collation = database.Collation;
            entity.Status.RecoveryModel = database.RecoveryModel;
            entity.Status.CompatibilityLevel = database.CompatibilityLevel;
            entity.Status.Owner = database.Owner;
            SetCondition(entity, "CollationMismatch", spec.Collation is { } c && !c.Equals(database.Collation, StringComparison.OrdinalIgnoreCase),
                "CollationDiffers", $"The database uses {database.Collation}, not {spec.Collation}: a collation cannot change in place.");
        }

        // A login created later (by a SqlUser) is picked up within 30 s, not at the next resync.
        var retry = ownerPending ? WaitInterval * 2 : (TimeSpan?)null;
        if (!onAg)
            return await FinishAsync(entity, Phases.Ready, "True", "DatabaseReady", $"Database '{spec.DatabaseName}' is ready on {server}", ct, retry);
        return await JoinAvailabilityGroupAsync(entity, targetName, ct, retry);
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

    /// <summary>
    /// Before creating on the primary of an AG: a copy of that name on another replica, outside the AG.
    /// The database was created on a primary that lost its role seconds later, before the
    /// join; the next pass created a second one on the new primary, and the seeding to the former
    /// primary failed for good ("Database With Name Already Exists"). A copy this resource owns is
    /// renamed out of the way (kept for a human, not dropped: something may have written to it); any
    /// other copy refuses the creation. A result when the creation must wait or is refused.
    /// </summary>
    private async Task<ReconciliationResult<SqlDatabase>?> StrayCopiesAsync(SqlDatabase entity, ResolvedTarget target, string primary,
        CancellationToken ct)
    {
        var dbName = entity.Spec.DatabaseName;
        foreach (var (host, port, replica) in await TargetConnections.EveryServerAsync(target, kubernetesClient, ct))
        {
            if (replica is null || replica == primary)
                continue;
            SqlConnection conn;
            try
            {
                conn = await connectionFactory.OpenAsync(host, port, target.SaPassword, SqlProfile.Ddl, "master", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Replica {Replica} not reachable while looking for copies of {Db}", replica, dbName);
                continue;
            }
            await using (conn)
            {
                if (await databases.GetDatabaseAsync(conn, dbName, ct) is not { } copy)
                    continue;
                if (copy.InAvailabilityGroup)
                    return await FinishAsync(entity, Phases.Pending, "False", "WaitingForTheAvailabilityGroup",
                        $"'{dbName}' is joined to the availability group on {replica} but missing on the primary {primary}", ct, WaitInterval);
                var tag = copy.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ? await databases.GetOwnerTagAsync(conn, dbName, ct) : null;
                if (tag != OwnerTag(entity))
                    return await FinishAsync(entity, Phases.Failed, "False", "DatabaseExistsOnAReplica",
                        $"{replica} holds a database named '{dbName}' outside the availability group: the seeding would fail. Drop or rename it there.",
                        ct, RetryInterval);
                var renamed = $"{dbName}_orphan_{_clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}";
                await Gateway.RenameDatabaseAsync(entity, databases, conn, dbName, renamed, replica, ct);
                var message = $"'{dbName}' was created on {replica} before a failover moved the primary to {primary}: that copy, outside the " +
                              $"availability group, was renamed '{renamed}' on {replica}. Inspect it, then drop it.";
                await eventPublisher.PublishAsync(entity, "OrphanCopyRenamed", message, "Warning", ct);
                SetCondition(entity, "OrphanCopy", true, "RenamedOnAReplica", message);
            }
        }
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

    /// <summary>Applies the declared options, each on its own: one that SQL Server refuses, or a file
    /// that would not fit on its volume, is reported (condition OptionsApplied, event) and does not stop
    /// the others (a failing MODIFY FILE failed the whole pass, again and again, while the
    /// status still said Ready). True while something waits (an owner login, an option to retry).</summary>
    private async Task<bool> ConvergeOptionsAsync(SqlDatabase entity, SqlConnection connection, DatabaseInfo database, ResolvedTarget target,
        string server, CancellationToken ct)
    {
        var spec = entity.Spec;
        var dbName = spec.DatabaseName;
        var problems = new List<string>();

        async Task Try(string what, Func<Task> apply)
        {
            try
            {
                await apply();
            }
            catch (SqlException ex)
            {
                logger.LogWarning(ex, "{What} of {Db} on {Server} failed", what, dbName, server);
                problems.Add($"{what}: {ex.Message}");
            }
        }

        if (spec.RecoveryModel is { } model && !SqlName(model).Equals(database.RecoveryModel, StringComparison.OrdinalIgnoreCase))
            await Try("recovery model", async () =>
            {
                await databases.SetRecoveryModelAsync(connection, dbName, SqlName(model), ct);
                await eventPublisher.PublishAsync(entity, "RecoveryModelChanged", $"'{dbName}': recovery model {database.RecoveryModel} → {SqlName(model)}", ct: ct);
            });

        if (spec.CompatibilityLevel is { } level && level != database.CompatibilityLevel)
            await Try("compatibility level", async () =>
            {
                await databases.SetCompatibilityLevelAsync(connection, dbName, level, ct);
                await eventPublisher.PublishAsync(entity, "CompatibilityLevelChanged", $"'{dbName}': compatibility level {database.CompatibilityLevel} → {level}", ct: ct);
            });

        var ownerMissing = false;
        if (spec.Owner is { } owner && !owner.Equals(database.Owner, StringComparison.OrdinalIgnoreCase))
        {
            if (await databases.LoginExistsAsync(connection, owner, ct))
                await Try("owner", async () =>
                {
                    await databases.SetOwnerAsync(connection, dbName, owner, ct);
                    await eventPublisher.PublishAsync(entity, "DatabaseOwnerChanged", $"'{dbName}': owner {database.Owner} → {owner}", ct: ct);
                });
            else
                ownerMissing = true;
        }
        SetCondition(entity, "OwnerLoginMissing", ownerMissing, "LoginNotFound",
            $"Login '{spec.Owner}' does not exist on {server} yet: the owner is applied once it does (a SqlUser creates it).");

        if (spec.Files is { } files)
        {
            var current = await databases.GetFilesAsync(connection, dbName, ct);
            var grows = new List<(DatabaseFileInfo File, long? Size)>();
            foreach (var (desired, type, volume) in new[] { (files.Data, 0, "data"), (files.Log, 1, "log") })
            {
                if (desired is null || current.FirstOrDefault(f => f.Type == type) is not { } file)
                    continue;
                var size = desired.Size is { } s && SqlDatabaseValidator.Bytes(s) is { } bytes && bytes > file.SizeBytes ? bytes : (long?)null;
                if (size is { } wanted && await ShortageAsync(target, volume, desired.Size!, wanted, ct) is { } shortage)
                {
                    problems.Add(shortage);
                    size = null;
                }
                var (growth, percent) = Growth(desired.Growth);
                var growthChange = growth is { } g && (g != file.Growth || percent != file.GrowthIsPercent) ? growth : null;
                var max = MaxSize(desired.MaxSize);
                var maxChange = max is { } m && m != file.MaxSizeBytes ? max : null;
                if (size is null && growthChange is null && maxChange is null)
                    continue;
                await Try($"{volume} file", async () =>
                {
                    await databases.ModifyFileAsync(connection, dbName, file.LogicalName, size, growthChange, percent, maxChange, ct);
                    if (size is not null)
                        grows.Add((file, size));
                });
            }
            if (grows.Count > 0)
                await eventPublisher.PublishAsync(entity, "DatabaseFilesGrown",
                    $"'{dbName}': {string.Join(", ", grows.Select(g => $"{g.File.LogicalName} → {g.Size / 1048576} MB"))}", ct: ct);
        }

        var failed = problems.Count > 0;
        var message = string.Join("; ", problems);
        var previous = entity.Status!.Conditions?.FirstOrDefault(c => c.Type == "OptionsApplied");
        if (failed && (previous is not { Status: "False" } || previous.Message != message))
            await eventPublisher.PublishAsync(entity, "OptionsNotApplied", $"'{dbName}' on {server}: {message}", "Warning", ct);
        if (failed)
            ConditionOps.Set(entity.Status!.Conditions ??= [], "OptionsApplied", "False", "OptionsNotApplied", message, now: _clock.GetUtcNow().UtcDateTime);
        else if (entity.Status!.Conditions?.Any(c => c.Type == "OptionsApplied") == true)
            ConditionOps.Set(entity.Status.Conditions, "OptionsApplied", "True", "OptionsApplied", "", now: _clock.GetUtcNow().UtcDateTime);
        return ownerMissing || failed;
    }

    /// <summary>A file size that would not fit on the volume of a server of the target: the message, or null.</summary>
    private async Task<string?> ShortageAsync(ResolvedTarget target, string volume, string declared, long bytes, CancellationToken ct)
    {
        var pods = target.IsAvailabilityGroup
            ? Enumerable.Range(0, target.ReplicaCount ?? 3).Select(i => $"{target.Name}-{i}")
            : [$"{target.Name}-0"];
        foreach (var pod in pods)
            if (await kubernetesClient.GetAsync<V1PersistentVolumeClaim>($"{volume}-{pod}", target.Namespace, ct) is { Status.Capacity: { } capacity }
                && capacity.TryGetValue("storage", out var quantity) && (long)quantity.ToDecimal() < bytes)
                return $"The {volume} file ({declared}) does not fit on the {volume} volume of {pod} ({quantity}): grow the volume first.";
        return null;
    }

    /// <summary>
    /// The SqlAvailabilityGroupDatabase that joins the database to its AG: created when missing,
    /// adopted when one already joins this database, kept in line with spec.availabilityGroup (the
    /// blocks set there only).
    /// </summary>
    private async Task<ReconciliationResult<SqlDatabase>> JoinAvailabilityGroupAsync(SqlDatabase entity, string agName, CancellationToken ct,
        TimeSpan? retry = null)
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
                $"Database '{spec.DatabaseName}' is synchronized in availability group '{agName}'", ct, retry)
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
        foreach (var (file, volume) in new[] { (files?.Data, "data"), (files?.Log, "log") })
            if (file?.Size is { } size && SqlDatabaseValidator.Bytes(size) is { } bytes
                && await ShortageAsync(target, volume, size, bytes, ct) is { } shortage)
                return shortage;
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
