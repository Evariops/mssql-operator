using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlTdeCertificate), Verbs = RbacVerb.All)]
public class SqlTdeCertificateController(
    ILogger<SqlTdeCertificateController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlTdeManager sqlTdeManager,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher) : IEntityController<SqlTdeCertificate>
{
    public async Task<ReconciliationResult<SqlTdeCertificate>> ReconcileAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlTdeCertificate {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var cert = entity.Spec.Certificate;
        if (cert == null) return ReconciliationResult<SqlTdeCertificate>.Success(entity);

        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);

        entity.Status ??= new SqlTdeCertificateStatus();
        entity.Status.TargetStatuses ??= [];

        if (resolvedTargets.Count == 0)
        {
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlTdeCertificate>.Success(entity);
        }

        var hasError = false;
        foreach (var target in resolvedTargets)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            try
            {
                if (target.Kind == "SqlAvailabilityGroup")
                {
                    // TDE on AG: cert on all replicas, encryption on primary only
                    await ProvisionAgTdeAsync(target, cert, entity, targetStatus, ct);
                }
                else
                {
                    await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);
                    await ProvisionInstanceTdeAsync(conn, cert, entity, targetStatus, ct);
                }

                targetStatus.LastError = null;
            }
            catch (Exception ex)
            {
                targetStatus.LastError = ex.Message;
                hasError = true;
                logger.LogWarning(ex, "Error deploying TDE on {Kind}/{Name}", target.Kind, target.Name);
            }
        }

        entity.Status.CertificateCreated = entity.Status.TargetStatuses.Any(t => t.CertificateCreated);
        SetCondition(entity, "Ready", hasError ? "False" : "True", hasError ? "PartialError" : "CertificateDeployed",
            $"Certificate deployed on {resolvedTargets.Count} target(s)");

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "CertificateDeployed", $"Certificate deployed on {resolvedTargets.Count} target(s)", ct: ct);
        return ReconciliationResult<SqlTdeCertificate>.Success(entity);
    }

    public Task<ReconciliationResult<SqlTdeCertificate>> DeletedAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("SqlTdeCertificate {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }

    private async Task ProvisionInstanceTdeAsync(Microsoft.Data.SqlClient.SqlConnection connection,
        CertificateSpec cert, SqlTdeCertificate entity, TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        if (!await sqlTdeManager.MasterKeyExistsAsync(connection, ct))
            await sqlTdeManager.CreateMasterKeyAsync(connection, entity.Spec.Target.ToString() ?? "default", ct);

        if (!await sqlTdeManager.CertificateExistsAsync(connection, cert.Name, ct))
        {
            if (cert.Source == "Generate" && cert.Generate != null)
                await sqlTdeManager.CreateCertificateAsync(connection, cert.Name,
                    cert.Generate.Subject ?? $"TDE Certificate {cert.Name}", cert.Generate.ExpiryDate, ct);
        }

        var encryptedDbs = new List<EncryptedDatabaseStatus>();
        if (entity.Spec.EncryptDatabases != null)
        {
            foreach (var db in entity.Spec.EncryptDatabases.Where(d => d.Enabled))
            {
                await sqlTdeManager.EnableDatabaseEncryptionAsync(connection, db.Name, cert.Name, db.EncryptionAlgorithm, ct);
                var state = await sqlTdeManager.GetDatabaseEncryptionStateAsync(connection, db.Name, ct);
                encryptedDbs.Add(new EncryptedDatabaseStatus { Name = db.Name, EncryptionState = state, EncryptionAlgorithm = db.EncryptionAlgorithm });
            }
        }

        targetStatus.CertificateCreated = true;
        targetStatus.Thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(connection, cert.Name, ct);
        targetStatus.EncryptedDatabases = encryptedDbs;
    }

    private async Task ProvisionAgTdeAsync(ResolvedTarget target, CertificateSpec cert,
        SqlTdeCertificate entity, TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        var replicaCount = target.ReplicaCount ?? 3;
        targetStatus.ReplicaStatuses ??= [];
        byte[]? certBytes = null;
        byte[]? keyBytes = null;

        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{target.Name}-{i}";
            var host = $"{replicaName}.{target.Namespace}.svc.cluster.local";
            var replicaStatus = targetStatus.ReplicaStatuses.FirstOrDefault(r => r.Replica == replicaName);
            if (replicaStatus == null)
            {
                replicaStatus = new TdeReplicaStatus { Replica = replicaName };
                targetStatus.ReplicaStatuses.Add(replicaStatus);
            }

            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);

                if (!await sqlTdeManager.MasterKeyExistsAsync(conn, ct))
                {
                    await sqlTdeManager.CreateMasterKeyAsync(conn, target.SaPassword, ct);
                    replicaStatus.MasterKeyCreated = true;
                }

                if (i == 0)
                {
                    // Create cert on first replica
                    if (!await sqlTdeManager.CertificateExistsAsync(conn, cert.Name, ct))
                    {
                        if (cert.Source == "Generate" && cert.Generate != null)
                            await sqlTdeManager.CreateCertificateAsync(conn, cert.Name,
                                cert.Generate.Subject ?? $"TDE Certificate {cert.Name}", cert.Generate.ExpiryDate, ct);
                    }

                    // Backup for propagation
                    (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateAsync(conn, cert.Name, ct);
                }
                else if (certBytes != null && keyBytes != null)
                {
                    // Import cert on other replicas
                    if (!await sqlTdeManager.CertificateExistsAsync(conn, cert.Name, ct))
                        await sqlTdeManager.RestoreCertificateAsync(conn, cert.Name, certBytes, keyBytes, ct);
                }

                replicaStatus.CertificateCreated = true;
                replicaStatus.Thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, cert.Name, ct);
                replicaStatus.LastError = null;
            }
            catch (Exception ex)
            {
                replicaStatus.LastError = ex.Message;
                logger.LogWarning(ex, "Error deploying TDE cert on replica {Replica}", replicaName);
            }
        }

        // Encrypt databases on primary only
        var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, replicaCount, ct);
        if (primary != null && entity.Spec.EncryptDatabases != null)
        {
            await using var primaryConn = primary.Value.Connection;
            var encryptedDbs = new List<EncryptedDatabaseStatus>();
            foreach (var db in entity.Spec.EncryptDatabases.Where(d => d.Enabled))
            {
                await sqlTdeManager.EnableDatabaseEncryptionAsync(primaryConn, db.Name, cert.Name, db.EncryptionAlgorithm, ct);
                var state = await sqlTdeManager.GetDatabaseEncryptionStateAsync(primaryConn, db.Name, ct);
                encryptedDbs.Add(new EncryptedDatabaseStatus { Name = db.Name, EncryptionState = state, EncryptionAlgorithm = db.EncryptionAlgorithm });
            }
            targetStatus.EncryptedDatabases = encryptedDbs;
        }

        targetStatus.CertificateCreated = targetStatus.ReplicaStatuses.All(r => r.CertificateCreated);
    }

    private static TdeCertificateTargetStatus GetOrCreateTargetStatus(SqlTdeCertificate entity, ResolvedTarget target)
    {
        var existing = entity.Status!.TargetStatuses!.FirstOrDefault(t => t.Kind == target.Kind && t.Name == target.Name && t.Namespace == target.Namespace);
        if (existing != null) return existing;

        var status = new TdeCertificateTargetStatus { Kind = target.Kind, Name = target.Name, Namespace = target.Namespace };
        entity.Status.TargetStatuses!.Add(status);
        return status;
    }

    private static void SetCondition(SqlTdeCertificate entity, string type, string status, string reason, string? message)
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
