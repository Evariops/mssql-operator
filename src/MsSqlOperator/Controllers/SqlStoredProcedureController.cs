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
    IEventPublisher eventPublisher,
    OperatorMetrics metrics) : IEntityController<SqlStoredProcedure>
{
    public async Task<ReconciliationResult<SqlStoredProcedure>> ReconcileAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlStoredProcedure {Name}", entity.Metadata.Name);

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
            return ReconciliationResult<SqlStoredProcedure>.Success(entity);
        }

        // Gather procedure definitions
        List<(string name, string schema, string sql, string hash)> procedures;
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
                ? TimeSpan.FromSeconds(Math.Clamp((resetAt - DateTimeOffset.UtcNow).TotalSeconds, 30, 3600))
                : TimeSpan.FromSeconds(60);
            return ReconciliationResult<SqlStoredProcedure>.Success(entity, retryAfter);
        }

        var deploymentOptions = entity.Spec.DeploymentOptions ?? new DeploymentOptionsSpec();
        var desiredProcedures = procedures.Count;
        var deployedProcedures = 0;
        var failedTargets = 0;

        foreach (var target in resolvedTargets)
        {
            try
            {
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
                    (targetDeployed, databaseCount) = await DeployProceduresAsync(conn, entity, procedures, deploymentOptions, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    (targetDeployed, databaseCount) = await DeployProceduresAsync(conn, entity, procedures, deploymentOptions, ct);
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
        // nothing — that must not read as full success.
        var nothingDeployed = procedures.Count > 0 && deployedProcedures == 0;

        var sourceRef = entity.Spec.GitHub != null
            ? $"{entity.Spec.GitHub.Repository}@{entity.Status.ResolvedCommitSha}"
            : entity.Spec.Inline != null && procedures.Count > 0
                ? storedProcManager.ComputeHash(string.Concat(procedures.Select(p => p.hash)))
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

        metrics.SetStoredProcedures(ns, name, deployedProcedures, desiredProcedures);

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        // Retry when something prevented deployment so the resource converges.
        return failedTargets > 0 || nothingDeployed
            ? ReconciliationResult<SqlStoredProcedure>.Success(entity, TimeSpan.FromSeconds(30))
            : ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }

    public Task<ReconciliationResult<SqlStoredProcedure>> DeletedAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("SqlStoredProcedure {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlStoredProcedure", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlStoredProcedure>.Success(entity));
    }

    private async Task<List<(string name, string schema, string sql, string hash)>> GatherProceduresAsync(SqlStoredProcedure entity, string ns, CancellationToken ct)
    {
        var procedures = new List<(string name, string schema, string sql, string hash)>();

        if (entity.Spec.Inline != null)
        {
            foreach (var proc in entity.Spec.Inline)
            {
                var hash = storedProcManager.ComputeHash(proc.Definition);
                procedures.Add((proc.Name, proc.Schema, proc.Definition, hash));
            }
        }

        if (entity.Spec.GitHub != null)
        {
            var github = entity.Spec.GitHub;
            var token = await ResolveGitHubTokenAsync(github, ns, ct);

            // Every fetch is pinned to a full commit SHA.
            // Tags and followLatestRelease are resolved here, at reconcile time; the spec is
            // never mutated (the tag is a local variable), and the resolved SHA is persisted
            // in status with an event on every change for traceability.
            var tag = github.Ref?.Tag;
            if (entity.Spec.Sync?.FollowLatestRelease == true)
            {
                var latestTag = await gitHubFetcher.GetLatestReleaseTagAsync(
                    github.Repository, entity.Spec.Sync.ReleaseFilter, token, ct);
                if (latestTag != null)
                    tag = latestTag;
            }

            var sha = github.Ref?.Sha;
            if (sha != null && !GitHubContentFetcher.IsFullCommitSha(sha))
                throw new GitHubFetchException($"ref.sha '{sha}' is not a full 40-hex commit SHA.");
            if (sha == null && tag != null)
                sha = await gitHubFetcher.ResolveTagToCommitShaAsync(github.Repository, tag, token, ct);
            if (sha == null)
                throw new GitHubFetchException("GitHub source is not pinned: set ref.sha (full commit SHA), ref.tag, or sync.followLatestRelease.");

            if (!string.Equals(entity.Status.ResolvedCommitSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                await eventPublisher.PublishAsync(entity, "SourceShaResolved",
                    $"GitHub source pinned at {github.Repository}@{sha}{(tag != null ? $" (tag {tag})" : "")}", ct: ct);
                entity.Status.ResolvedCommitSha = sha;
            }

            var files = await gitHubFetcher.FetchFilesAsync(github, sha, token, ct);
            foreach (var (path, content) in files)
            {
                var parsed = storedProcManager.ParseProcedureName(content);
                var name = parsed?.name ?? Path.GetFileNameWithoutExtension(path);
                var schema = parsed?.schema ?? "dbo";
                var hash = storedProcManager.ComputeHash(content);
                procedures.Add((name, schema, content, hash));
            }
        }

        return procedures;
    }

    /// <summary>
    /// Deploys every procedure to every matched database, relying on <c>CREATE OR ALTER</c>
    /// semantics for idempotency (no per-procedure hash skipping against status). Returns the
    /// number of procedure deployments applied and the count of databases matched.
    /// </summary>
    private async Task<(int deployed, int databases)> DeployProceduresAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, SqlStoredProcedure entity,
        List<(string name, string schema, string sql, string hash)> procedures,
        DeploymentOptionsSpec deployOptions, CancellationToken ct)
    {
        var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);
        var targetDbs = databaseFilter.Apply(allDatabases, entity.Spec.Databases, entity.Spec.DatabaseFilter);
        var deployed = 0;
        var databaseCount = 0;

        foreach (var db in targetDbs)
        {
            databaseCount++;
            var ordered = deployOptions.ExecutionOrder == "Alphabetical"
                ? procedures.OrderBy(p => p.name).ToList()
                : procedures;

            if (deployOptions.Transactional)
            {
                var statementsToRun = ordered.Select(p => p.sql).ToList();

                if (statementsToRun.Count > 0)
                {
                    if (deployOptions.ValidateSyntax)
                        foreach (var sql in statementsToRun)
                            if (!await storedProcManager.ValidateSyntaxAsync(connection, db, sql, ct))
                                return (deployed, databaseCount);

                    if (!deployOptions.DryRun)
                    {
                        await storedProcManager.DeployInTransactionAsync(connection, db, statementsToRun, ct);
                        deployed += statementsToRun.Count;
                    }
                }
            }
            else
            {
                foreach (var proc in ordered)
                {
                    if (!deployOptions.DryRun)
                    {
                        if (deployOptions.Strategy == "DropAndCreate")
                            await storedProcManager.DropProcedureAsync(connection, db, proc.schema, proc.name, ct);
                        await storedProcManager.DeployProcedureAsync(connection, db, proc.sql, ct);
                        deployed++;
                    }
                }
            }
        }

        return (deployed, databaseCount);
    }

    private async Task<string?> ResolveGitHubTokenAsync(GitHubSourceSpec github, string ns, CancellationToken ct)
    {
        if (github.AuthSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(github.AuthSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            return secret.Data.TryGetValue(github.AuthSecretRef.Key, out var bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch { return null; }
    }

    private static void SetCondition(SqlStoredProcedure entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);
}
