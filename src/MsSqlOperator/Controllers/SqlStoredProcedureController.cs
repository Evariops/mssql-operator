using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.GitHub;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlStoredProcedure), Verbs = RbacVerb.All)]
public class SqlStoredProcedureController(
    ILogger<SqlStoredProcedureController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlStoredProcedureManager storedProcManager,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    IGitHubContentFetcher gitHubFetcher) : IEntityController<SqlStoredProcedure>
{
    public async Task<ReconciliationResult<SqlStoredProcedure>> ReconcileAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlStoredProcedure {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var (host, port, password) = await ResolveTargetAsync(entity, ns, ct);
        if (host == null || password == null) return ReconciliationResult<SqlStoredProcedure>.Success(entity);

        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        // Resolve target databases
        var allDatabases = await sqlServerManager.GetDatabaseNamesAsync(connection, ct);
        var targetDbs = databaseFilter.Apply(allDatabases, entity.Spec.Databases, entity.Spec.DatabaseFilter);

        // Gather procedure definitions
        var procedures = new List<(string name, string schema, string sql, string hash)>();

        // Inline procedures
        if (entity.Spec.Inline != null)
        {
            foreach (var proc in entity.Spec.Inline)
            {
                var hash = storedProcManager.ComputeHash(proc.Definition);
                procedures.Add((proc.Name, proc.Schema, proc.Definition, hash));
            }
        }

        // GitHub procedures
        if (entity.Spec.GitHub != null)
        {
            var token = await ResolveGitHubTokenAsync(entity.Spec.GitHub, ns, ct);

            // Check for latest release if configured
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

        // Deploy to each target database
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
                    var existing = FindExistingProcHash(entity.Status, db, proc.name, proc.schema);
                    if (existing != proc.hash)
                    {
                        statementsToRun.Add(proc.sql);
                    }
                }

                if (statementsToRun.Count > 0)
                {
                    if (deployOptions.ValidateSyntax)
                    {
                        foreach (var sql in statementsToRun)
                        {
                            if (!await storedProcManager.ValidateSyntaxAsync(connection, db, sql, ct))
                            {
                                logger.LogError("Syntax validation failed for procedure in database {Db}", db);
                                return ReconciliationResult<SqlStoredProcedure>.Success(entity);
                            }
                        }
                    }

                    if (!deployOptions.DryRun)
                    {
                        await storedProcManager.DeployInTransactionAsync(connection, db, statementsToRun, ct);
                    }
                }
            }
            else
            {
                foreach (var proc in ordered)
                {
                    var existing = FindExistingProcHash(entity.Status, db, proc.name, proc.schema);
                    if (existing != proc.hash && !deployOptions.DryRun)
                    {
                        if (deployOptions.Strategy == "DropAndCreate")
                        {
                            await storedProcManager.DropProcedureAsync(connection, db, proc.schema, proc.name, ct);
                        }
                        await storedProcManager.DeployProcedureAsync(connection, db, proc.sql, ct);
                    }
                }
            }

            foreach (var proc in ordered)
            {
                dbProcs.Add(new DeployedProcedureInfo
                {
                    Name = proc.name, Schema = proc.schema,
                    Deployed = true, Hash = proc.hash,
                    LastDeployed = DateTime.UtcNow
                });
            }

            deployedProcedures.Add(new DeployedDatabaseProcedures { Database = db, Procedures = dbProcs });
        }

        // Update status
        entity.Status ??= new SqlStoredProcedureStatus();
        entity.Status.Phase = "Deployed";
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Source = entity.Spec.Inline != null ? "Inline" : "GitHub";
        entity.Status.SourceRef = entity.Spec.GitHub != null
            ? $"{entity.Spec.GitHub.Repository}@{entity.Spec.GitHub.Ref?.Tag ?? entity.Spec.GitHub.Ref?.Sha ?? "HEAD"}"
            : null;
        entity.Status.DeployedProcedures = deployedProcedures;
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready", Status = "True", Reason = "AllProceduresDeployed",
                Message = $"{procedures.Count} procedures deployed across {targetDbs.Count} databases",
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);

        logger.LogInformation("SqlStoredProcedure {Name} reconciled: {Count} procedures across {DbCount} databases",
            entity.Metadata.Name, procedures.Count, targetDbs.Count);
        return ReconciliationResult<SqlStoredProcedure>.Success(entity);
    }

    public Task<ReconciliationResult<SqlStoredProcedure>> DeletedAsync(SqlStoredProcedure entity, CancellationToken ct)
    {
        logger.LogInformation("SqlStoredProcedure {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlStoredProcedure>.Success(entity));
    }

    private static string? FindExistingProcHash(SqlStoredProcedureStatus? status, string database, string name, string schema)
    {
        return status?.DeployedProcedures?
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

    private async Task<(string? host, int port, string? password)> ResolveTargetAsync(SqlStoredProcedure entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.InstanceRef == null) return (null, 1433, null);
        var instance = await kubernetesClient.GetAsync<SqlInstance>(entity.Spec.InstanceRef.Name, ns, ct);
        if (instance?.Spec.SaPasswordSecretRef == null) return (null, 1433, null);
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(instance.Spec.SaPasswordSecretRef.Name, ns, ct);
            var pwd = secret?.Data?.TryGetValue(instance.Spec.SaPasswordSecretRef.Key, out var bytes) == true
                ? System.Text.Encoding.UTF8.GetString(bytes) : null;
            return ($"{entity.Spec.InstanceRef.Name}.{ns}.svc.cluster.local", instance.Spec.Service?.Port ?? 1433, pwd);
        }
        catch { return (null, 1433, null); }
    }
}
