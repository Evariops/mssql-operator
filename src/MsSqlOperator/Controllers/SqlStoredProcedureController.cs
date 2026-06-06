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
    IEventPublisher eventPublisher) : IEntityController<SqlStoredProcedure>
{
    public async Task<ReconciliationResult<SqlStoredProcedure>> ReconcileAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlStoredProcedure {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        entity.Status ??= new SqlStoredProcedureStatus();
        entity.Status.TargetStatuses ??= [];

        if (resolvedTargets.Count == 0)
        {
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlStoredProcedure>.Success(entity);
        }

        // Gather procedure definitions
        var procedures = await GatherProceduresAsync(entity, ns, ct);

        var hasError = false;
        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            try
            {
                // For AG, find primary (stored procs only run on primary)
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, target.ReplicaCount ?? 3, ct);
                    if (primary == null)
                    {
                        targetStatus.LastError = "No primary replica found";
                        hasError = true;
                        continue;
                    }

                    await using var conn = primary.Value.Connection;
                    targetStatus.DeployedProcedures = await DeployProceduresAsync(conn, entity, procedures, targetStatus, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    targetStatus.DeployedProcedures = await DeployProceduresAsync(conn, entity, procedures, targetStatus, ct);
                }

                targetStatus.LastError = null;
            }
            catch (Exception ex)
            {
                targetStatus.LastError = ex.Message;
                hasError = true;
                logger.LogWarning(ex, "Error deploying procedures on {Kind}/{Name}", target.Kind, target.Name);
            }
        }

        entity.Status.Phase = hasError ? "PartiallyDeployed" : "Deployed";
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Source = entity.Spec.Inline != null ? "Inline" : "GitHub";
        entity.Status.SourceRef = entity.Spec.GitHub != null
            ? $"{entity.Spec.GitHub.Repository}@{entity.Spec.GitHub.Ref?.Tag ?? entity.Spec.GitHub.Ref?.Sha ?? "HEAD"}"
            : null;

        SetCondition(entity, "Ready", hasError ? "False" : "True", hasError ? "PartialError" : "AllProceduresDeployed",
            $"{procedures.Count} procedures deployed across {resolvedTargets.Count} target(s)");

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "ProceduresDeployed", $"{procedures.Count} procedures deployed across {resolvedTargets.Count} target(s)", ct: ct);
        return ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }

    public Task<ReconciliationResult<SqlStoredProcedure>> DeletedAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("SqlStoredProcedure {Name} deleted", entity.Metadata.Name);
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
            var token = await ResolveGitHubTokenAsync(entity.Spec.GitHub, ns, ct);

            if (entity.Spec.Sync?.FollowLatestRelease == true)
            {
                var latestTag = await gitHubFetcher.GetLatestReleaseTagAsync(
                    entity.Spec.GitHub.Repository, entity.Spec.Sync.ReleaseFilter, token, ct);
                if (latestTag != null)
                {
                    entity.Spec.GitHub.Ref ??= new GitHubRefSpec();
                    entity.Spec.GitHub.Ref.Tag = latestTag;
                }
            }

            var files = await gitHubFetcher.FetchFilesAsync(entity.Spec.GitHub, token, ct);
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

    private async Task<List<DeployedDatabaseProcedures>> DeployProceduresAsync(
        Microsoft.Data.SqlClient.SqlConnection connection, SqlStoredProcedure entity,
        List<(string name, string schema, string sql, string hash)> procedures,
        StoredProcedureTargetStatus targetStatus, CancellationToken ct)
    {
        var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);
        var targetDbs = databaseFilter.Apply(allDatabases, entity.Spec.Databases, entity.Spec.DatabaseFilter);
        var deployedProcedures = new List<DeployedDatabaseProcedures>();
        var deployOptions = entity.Spec.DeploymentOptions ?? new DeploymentOptionsSpec();

        foreach (var db in targetDbs)
        {
            var dbProcs = new List<DeployedProcedureInfo>();
            var ordered = deployOptions.ExecutionOrder == "Alphabetical"
                ? procedures.OrderBy(p => p.name).ToList()
                : procedures;

            if (deployOptions.Transactional)
            {
                var statementsToRun = new List<string>();
                foreach (var proc in ordered)
                {
                    var existing = FindExistingProcHash(targetStatus, db, proc.name, proc.schema);
                    if (existing != proc.hash)
                        statementsToRun.Add(proc.sql);
                }

                if (statementsToRun.Count > 0)
                {
                    if (deployOptions.ValidateSyntax)
                        foreach (var sql in statementsToRun)
                            if (!await storedProcManager.ValidateSyntaxAsync(connection, db, sql, ct))
                                return deployedProcedures;

                    if (!deployOptions.DryRun)
                        await storedProcManager.DeployInTransactionAsync(connection, db, statementsToRun, ct);
                }
            }
            else
            {
                foreach (var proc in ordered)
                {
                    var existing = FindExistingProcHash(targetStatus, db, proc.name, proc.schema);
                    if (existing != proc.hash && !deployOptions.DryRun)
                    {
                        if (deployOptions.Strategy == "DropAndCreate")
                            await storedProcManager.DropProcedureAsync(connection, db, proc.schema, proc.name, ct);
                        await storedProcManager.DeployProcedureAsync(connection, db, proc.sql, ct);
                    }
                }
            }

            foreach (var proc in ordered)
                dbProcs.Add(new DeployedProcedureInfo
                {
                    Name = proc.name, Schema = proc.schema, Deployed = true,
                    Hash = proc.hash, LastDeployed = DateTime.UtcNow
                });

            deployedProcedures.Add(new DeployedDatabaseProcedures { Database = db, Procedures = dbProcs });
        }

        return deployedProcedures;
    }

    private static string? FindExistingProcHash(StoredProcedureTargetStatus targetStatus, string database, string name, string schema)
    {
        return targetStatus.DeployedProcedures?
            .FirstOrDefault(d => d.Database == database)?
            .Procedures?
            .FirstOrDefault(p => p.Name == name && p.Schema == schema)?
            .Hash;
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

    private static StoredProcedureTargetStatus GetOrCreateTargetStatus(SqlStoredProcedure entity, ResolvedTarget target)
    {
        var existing = entity.Status!.TargetStatuses!.FirstOrDefault(t => t.Kind == target.Kind && t.Name == target.Name && t.Namespace == target.Namespace);
        if (existing != null) return existing;

        var status = new StoredProcedureTargetStatus { Kind = target.Kind, Name = target.Name, Namespace = target.Namespace };
        entity.Status.TargetStatuses!.Add(status);
        return status;
    }

    private static void SetCondition(SqlStoredProcedure entity, string type, string status, string reason, string? message)
    {
        entity.Status!.Conditions ??= [];
        var existing = entity.Status.Conditions.FirstOrDefault(c => c.Type == type);
        if (existing != null)
        {
            existing.Status = status; existing.Reason = reason; existing.Message = message; existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            entity.Status.Conditions.Add(new ConditionStatus { Type = type, Status = status, Reason = reason, Message = message, LastTransitionTime = DateTime.UtcNow });
        }
    }
}
