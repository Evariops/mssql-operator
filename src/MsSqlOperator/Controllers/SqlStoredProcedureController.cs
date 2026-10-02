using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.GitHub;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlStoredProcedure), Verbs = RbacVerb.All)]
public class SqlStoredProcedureController(
    ILogger<SqlStoredProcedureController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlStoredProcedureManager storedProcManager,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    IGitHubContentFetcher gitHubFetcher,
    ITargetResolver targetResolver,
    IProcDeployerLoginManager procDeployerLoginManager,
    IEventPublisher eventPublisher,
    OperatorMetrics metrics,
    TimeProvider? timeProvider = null) : IEntityController<SqlStoredProcedure>
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ReconciliationResult<SqlStoredProcedure>> ReconcileAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlStoredProcedure {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        entity.Status ??= new SqlStoredProcedureStatus();

        if (resolvedTargets.Count == 0)
        {
            entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
            entity.Status.Targets = 0;
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            metrics.SetStoredProcedures(ns, name, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            // Keep polling until a target appears.
            return ReconciliationResult<SqlStoredProcedure>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // Gather procedure definitions
        List<ProcedureDefinition> procedures;
        try
        {
            procedures = await GatherProceduresAsync(entity, ns, ct);
        }
        catch (GitHubFetchException ex)
        {
            // Typed source failure: never report a partial fetch as success.
            logger.LogWarning(ex, "GitHub source unavailable for SqlStoredProcedure {Name}", name);
            SetCondition(entity, "Ready", "False", "SourceUnavailable", ex.Message);
            await eventPublisher.PublishAsync(entity, "SourceUnavailable", ex.Message, "Warning", ct);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            var retryAfter = ex is GitHubRateLimitException { ResetAt: { } resetAt }
                ? TimeSpan.FromSeconds(Math.Clamp((resetAt - _clock.GetUtcNow()).TotalSeconds, 30, 3600))
                : TimeSpan.FromSeconds(60);
            return ReconciliationResult<SqlStoredProcedure>.Success(entity, retryAfter);
        }
        catch (ProcedureScriptException ex)
        {
            // A source that is not made only of procedure definitions is never executed.
            logger.LogWarning("SqlStoredProcedure {Name} refused: {Reason}", name, ex.Message);
            entity.Status.Phase = "Failed";
            SetCondition(entity, "Ready", "False", "InvalidProcedureScript", ex.Message);
            await eventPublisher.PublishAsync(entity, "InvalidProcedureScript", ex.Message, "Warning", ct);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlStoredProcedure>.Success(entity, SyncInterval(entity));
        }

        var deploymentOptions = entity.Spec.DeploymentOptions ?? new DeploymentOptionsSpec();
        var desiredProcedures = procedures.Count;
        var deployedProcedures = 0;
        var failedTargets = 0;
        var owner = $"{ns}/{name}";

        foreach (var target in resolvedTargets)
        {
            try
            {
                // Deploying as sa hands sysadmin to every author of a SqlStoredProcedure in the
                // namespace (EXECUTE AS SELF/OWNER in the body runs as dbo). The target must opt in.
                if (deploymentOptions.DeployAs == "Sa" && !await TargetAllowsSaDeploymentAsync(target, ct))
                {
                    failedTargets++;
                    var refusal = $"deployAs: Sa refused on {target.Kind}/{target.Name}: the target does not carry the annotation " +
                                  $"{SqlIdentifierRules.AllowSaDeploymentsAnnotation}=\"true\". Use deployAs: LeastPrivilege.";
                    SetCondition(entity, "SaDeploymentDenied", "True", "TargetOptInMissing", refusal);
                    await eventPublisher.PublishAsync(entity, "SaDeploymentDenied", refusal, "Warning", ct);
                    continue;
                }

                int targetDeployed;
                int databaseCount;
                // For AG, find primary (stored procs only run on primary)
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null)
                    {
                        failedTargets++;
                        logger.LogWarning("No primary replica found for {Kind}/{Name}", target.Kind, target.Name);
                        await eventPublisher.PublishAsync(entity, "TargetUnreachable",
                            $"No primary replica found on {target.Kind}/{target.Name}", "Warning", ct);
                        continue;
                    }

                    await using var conn = primary.Value.Connection;
                    (targetDeployed, databaseCount) = await DeployProceduresAsync(target, conn, entity, procedures, deploymentOptions, owner, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    (targetDeployed, databaseCount) = await DeployProceduresAsync(target, conn, entity, procedures, deploymentOptions, owner, ct);
                }

                deployedProcedures += targetDeployed;
                await eventPublisher.PublishAsync(entity, "ProceduresDeployed",
                    $"{targetDeployed} procedures across {databaseCount} databases on {target.Kind}/{target.Name}", ct: ct);
            }
            catch (Exception ex)
            {
                failedTargets++;
                logger.LogWarning(ex, "Error deploying procedures on {Kind}/{Name}", target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "DeployFailed",
                    $"Failed deploying procedures on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        // A target can connect cleanly yet match zero databases (filter/explicit list), deploying
        // nothing — that must not read as full success. DryRun deploys nothing by
        // design and must NOT loop on the aggressive 30s requeue (it re-fetched GitHub forever).
        var dryRun = deploymentOptions.DryRun;
        var nothingDeployed = !dryRun && procedures.Count > 0 && deployedProcedures == 0;

        var sourceRef = entity.Spec.GitHub != null
            ? $"{entity.Spec.GitHub.Repository}@{entity.Status.ResolvedCommitSha}"
            : entity.Spec.Inline != null && procedures.Count > 0
                ? storedProcManager.ComputeHash(string.Concat(procedures.Select(p => p.Hash)))
                : null;

        entity.Status.Phase = failedTargets > 0 ? "PartiallyDeployed" : nothingDeployed ? "Pending" : "Deployed";
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Source = entity.Spec.Inline != null ? "Inline" : "GitHub";
        entity.Status.SourceRef = sourceRef;
        entity.Status.Targets = resolvedTargets.Count;
        entity.Status.FailedTargets = failedTargets;
        entity.Status.DesiredProcedures = desiredProcedures;
        entity.Status.DeployedProcedures = deployedProcedures;
        if (!nothingDeployed && failedTargets == 0)
            entity.Status.LastDeployedRevision = sourceRef;

        var (status, reason, message) = failedTargets > 0
            ? ("False", "PartialError", $"{deployedProcedures} of {desiredProcedures} procedures deployed across {resolvedTargets.Count} target(s); {failedTargets} target(s) failed")
            : nothingDeployed
                ? ("False", "NoDatabasesMatched", $"{desiredProcedures} procedure(s) defined but no target database matched; nothing deployed")
                : ("True", "AllProceduresDeployed", $"{deployedProcedures} procedure deployment(s) across {resolvedTargets.Count} target(s)");
        SetCondition(entity, "Ready", status, reason, message);
        if (deploymentOptions.DeployAs != "Sa" || failedTargets == 0)
            SetCondition(entity, "SaDeploymentDenied", "False", "NotDenied", null);

        metrics.SetStoredProcedures(ns, name, deployedProcedures, desiredProcedures);

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        // Retry when something prevented deployment so the resource converges; a GitHub source is
        // otherwise re-checked at its sync interval.
        return failedTargets > 0 || nothingDeployed
            ? ReconciliationResult<SqlStoredProcedure>.Success(entity, TimeSpan.FromSeconds(30))
            : entity.Spec.GitHub != null
                ? ReconciliationResult<SqlStoredProcedure>.Success(entity, SyncInterval(entity))
                : ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }

    /// <summary>How often a GitHub source is re-resolved (sync.intervalMinutes used to be ignored).</summary>
    private static TimeSpan SyncInterval(SqlStoredProcedure entity)
        => TimeSpan.FromMinutes(Math.Max(1, entity.Spec.Sync?.IntervalMinutes ?? 30));

    public Task<ReconciliationResult<SqlStoredProcedure>> DeletedAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("SqlStoredProcedure {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlStoredProcedure", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlStoredProcedure>.Success(entity));
    }

    private async Task<List<ProcedureDefinition>> GatherProceduresAsync(SqlStoredProcedure entity, string ns, CancellationToken ct)
    {
        var procedures = new List<ProcedureDefinition>();

        foreach (var proc in entity.Spec.Inline ?? [])
        {
            // An inline definition is exactly one procedure, the one the spec names.
            var definitions = ProcedureScript.Parse(proc.Definition);
            if (definitions is not [var definition]
                || !definition.Schema.Equals(proc.Schema, StringComparison.OrdinalIgnoreCase)
                || !definition.Name.Equals(proc.Name, StringComparison.OrdinalIgnoreCase))
                throw new ProcedureScriptException(
                    $"inline procedure '{proc.Schema}.{proc.Name}' must define exactly that procedure " +
                    $"(found: {string.Join(", ", definitions.Select(d => $"{d.Schema}.{d.Name}"))}).");
            procedures.Add(definition);
        }

        if (entity.Spec.GitHub != null)
        {
            var github = entity.Spec.GitHub;
            var token = await ResolveGitHubTokenAsync(github, ns, ct);
            var sha = await ResolveCommitShaAsync(entity, github, token, ct);

            var files = await gitHubFetcher.FetchFilesAsync(github, sha, token, ct);
            foreach (var (path, content) in files)
            {
                try
                {
                    procedures.AddRange(ProcedureScript.Parse(content));
                }
                catch (ProcedureScriptException ex)
                {
                    throw new ProcedureScriptException($"{path}: {ex.Message}");
                }
            }
        }

        var duplicate = procedures.GroupBy(p => $"{p.Schema}.{p.Name}", StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ProcedureScriptException($"procedure '{duplicate.Key}' is defined more than once.");
        return procedures;
    }

    /// <summary>
    /// Every fetch is pinned to a full commit SHA. Tags and
    /// followLatestRelease are resolved here, at reconcile time; the spec is never mutated, and the
    /// resolved SHA is persisted in status with an event on every change for traceability.
    /// A mutable ref is re-resolved once per sync.intervalMinutes, not on every pass — each
    /// resolution costs GitHub API calls (60/h unauthenticated).
    /// </summary>
    private async Task<string> ResolveCommitShaAsync(SqlStoredProcedure entity, GitHubSourceSpec github, string? token, CancellationToken ct)
    {
        var status = entity.Status!;
        var sha = github.Ref?.Sha;
        if (sha != null)
        {
            if (!GitHubContentFetcher.IsFullCommitSha(sha))
                throw new GitHubFetchException($"ref.sha '{sha}' is not a full 40-hex commit SHA.");
        }
        else if (status.ResolvedCommitSha != null
                 && status.ObservedGeneration == (entity.Metadata.Generation ?? 0)
                 && status.LastSourceSyncTime is { } lastSync
                 && _clock.GetUtcNow().UtcDateTime - lastSync < SyncInterval(entity))
        {
            return status.ResolvedCommitSha;
        }
        else
        {
            var tag = github.Ref?.Tag;
            if (entity.Spec.Sync?.FollowLatestRelease == true)
            {
                var latestTag = await gitHubFetcher.GetLatestReleaseTagAsync(
                    github.Repository, entity.Spec.Sync.ReleaseFilter, token, ct);
                if (latestTag != null)
                    tag = latestTag;
            }

            if (tag != null)
                sha = await gitHubFetcher.ResolveTagToCommitShaAsync(github.Repository, tag, token, ct);
            if (sha == null)
                throw new GitHubFetchException("GitHub source is not pinned: set ref.sha (full commit SHA), ref.tag, or sync.followLatestRelease.");
            status.LastSourceSyncTime = _clock.GetUtcNow().UtcDateTime;
        }

        if (!string.Equals(status.ResolvedCommitSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            await eventPublisher.PublishAsync(entity, "SourceShaResolved",
                $"GitHub source pinned at {github.Repository}@{sha}{(github.Ref?.Tag is { } t ? $" (tag {t})" : "")}", ct: ct);
            status.ResolvedCommitSha = sha;
        }

        return sha;
    }

    /// <summary>
    /// Deploys every procedure to every matched database. Returns the number of procedures that are
    /// current there (deployed this pass or already up to date) and the count of databases matched.
    /// </summary>
    /// <remarks>
    /// <paramref name="saConnection"/> enumerates databases, reads the deployment markers and
    /// (for LeastPrivilege) provisions the deployer login; the actual DDL runs on a connection opened
    /// as that least-privilege login, holding db_ddladmin only for the duration of the deployment
    /// With <c>deployAs: Sa</c> (target opt-in required) the sa connection carries the DDL.
    /// A procedure whose marker says it is unchanged on both sides is not redeployed — a
    /// CREATE OR ALTER of every procedure on every resync recompiled hot procedures and took Sch-M
    /// locks every few minutes.
    /// </remarks>
    private async Task<(int deployed, int databases)> DeployProceduresAsync(
        ResolvedTarget target, Microsoft.Data.SqlClient.SqlConnection saConnection, SqlStoredProcedure entity,
        List<ProcedureDefinition> procedures, DeploymentOptionsSpec deployOptions, string owner, CancellationToken ct)
    {
        var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(saConnection, ct);
        var targetDbs = databaseFilter.Apply(allDatabases, entity.Spec.Databases, entity.Spec.DatabaseFilter);
        var ordered = deployOptions.ExecutionOrder == "Alphabetical"
            ? procedures.OrderBy(p => p.Name, StringComparer.Ordinal).ToList()
            : procedures;

        var current = 0;
        var plan = new List<(string Database, List<ProcedureDefinition> Procedures)>();
        foreach (var db in targetDbs)
        {
            var states = (await storedProcManager.GetDeploymentStatesAsync(saConnection, db, ct))
                .ToDictionary(st => $"{st.Schema}.{st.Name}", StringComparer.OrdinalIgnoreCase);
            var changed = new List<ProcedureDefinition>();
            foreach (var proc in ordered)
            {
                if (states.TryGetValue($"{proc.Schema}.{proc.Name}", out var state)
                    && ProcedureMarker.Parse(state.Marker) is { } marker)
                {
                    if (marker.Owner != owner)
                        throw new InvalidOperationException(
                            $"Procedure {proc.Schema}.{proc.Name} in database '{db}' is managed by SqlStoredProcedure '{marker.Owner}'; refusing to overwrite it.");
                    if (deployOptions.Strategy != "DropAndCreate" && marker.IsCurrent(owner, proc.Hash, state.DefinitionHash))
                    {
                        current++;
                        continue;
                    }
                }

                changed.Add(proc);
            }

            if (changed.Count > 0)
                plan.Add((db, changed));
        }

        if (plan.Count == 0 || deployOptions.DryRun)
        {
            if (deployOptions.ValidateSyntax)
                foreach (var (db, changed) in plan)
                    await ValidateSyntaxAsync(saConnection, db, changed, ct);
            return (current, targetDbs.Count);
        }

        // Deploy as the least-privilege db_ddladmin login by default, provisioned only when
        // DDL will actually run.
        IProcDeployerLease? lease = null;
        Microsoft.Data.SqlClient.SqlConnection deployConnection = saConnection;
        try
        {
            if (deployOptions.DeployAs != "Sa")
            {
                lease = await procDeployerLoginManager.AcquireAsync(target, saConnection, plan.Select(p => p.Database).ToList(), ct);
                // The deployer connects where the sa connection is: an instance, or an AG primary pod.
                var (host, port) = SplitDataSource(saConnection.DataSource,
                    target.Kind == "SqlAvailabilityGroup" ? SqlEndpoints.ContainerPort : target.Port);
                deployConnection = await connectionFactory.CreateConnectionAsync(host, port, lease.Password, "master", ct,
                    user: procDeployerLoginManager.LoginName);
            }

            foreach (var (db, changed) in plan)
            {
                if (deployOptions.ValidateSyntax)
                    await ValidateSyntaxAsync(deployConnection, db, changed, ct);

                if (deployOptions.Transactional)
                {
                    await storedProcManager.DeployInTransactionAsync(deployConnection, db, changed, ct);
                }
                else
                {
                    foreach (var proc in changed)
                    {
                        if (deployOptions.Strategy == "DropAndCreate")
                            await storedProcManager.DropProcedureAsync(deployConnection, db, proc.Schema, proc.Name, ct);
                        await storedProcManager.DeployProcedureAsync(deployConnection, db, proc, ct);
                    }
                }

                // The marker records the owner and both hashes; the sa connection reads definitions
                // the deployer login may not be allowed to see.
                foreach (var proc in changed)
                    await storedProcManager.MarkDeployedAsync(saConnection, db, proc.Schema, proc.Name, owner, proc.Hash, ct);
                current += changed.Count;
            }
        }
        finally
        {
            if (!ReferenceEquals(deployConnection, saConnection))
                await deployConnection.DisposeAsync();
            if (lease is not null)
                await lease.DisposeAsync();
        }

        return (current, targetDbs.Count);
    }

    private async Task ValidateSyntaxAsync(Microsoft.Data.SqlClient.SqlConnection connection, string db,
        List<ProcedureDefinition> procedures, CancellationToken ct)
    {
        foreach (var proc in procedures)
            if (!await storedProcManager.ValidateSyntaxAsync(connection, db, proc, ct))
                // A syntax failure is a target failure — the caller counts it and emits an
                // event. Silently returning made the status read "Deployed" while nothing was deployed.
                throw new InvalidOperationException($"Syntax validation failed for procedure {proc.Schema}.{proc.Name} in database '{db}'.");
    }

    /// <summary>Reads the opt-in annotation on the resolved target CR (fresh GET).</summary>
    private async Task<bool> TargetAllowsSaDeploymentAsync(ResolvedTarget target, CancellationToken ct)
    {
        IDictionary<string, string>? annotations = target.Kind switch
        {
            "SqlInstance" => (await kubernetesClient.GetAsync<SqlInstance>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            "SqlAvailabilityGroup" => (await kubernetesClient.GetAsync<SqlAvailabilityGroup>(target.Name, target.Namespace, ct))?.Metadata?.Annotations,
            _ => null,
        };
        return SqlIdentifierRules.AnnotationEnabled(annotations, SqlIdentifierRules.AllowSaDeploymentsAnnotation);
    }

    private async Task<string?> ResolveGitHubTokenAsync(GitHubSourceSpec github, string ns, CancellationToken ct)
    {
        if (github.AuthSecretRef == null) return null;
        // Null only when the Secret is absent — an API error propagates instead of silently
        // downgrading to anonymous calls (and their 60 requests/hour).
        return await SecretValues.TryGetAsync(kubernetesClient, github.AuthSecretRef, ns, ct);
    }

    // The deployer connection must land on the very server the sa connection is on (the AG
    // primary, not the listener). SqlConnection.DataSource is "host,port" as built by the
    // factory; fall back to the resolved port if it carries none.
    private static (string host, int port) SplitDataSource(string dataSource, int fallbackPort)
    {
        var comma = dataSource.LastIndexOf(',');
        if (comma < 0)
            return (dataSource, fallbackPort);
        var host = dataSource[..comma];
        return int.TryParse(dataSource[(comma + 1)..], out var port) ? (host, port) : (host, fallbackPort);
    }

    private static void SetCondition(SqlStoredProcedure entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);
}
