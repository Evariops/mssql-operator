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

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlTdeCertificate), Verbs = RbacVerb.All)]
public class SqlTdeCertificateController(
    ILogger<SqlTdeCertificateController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlTdeManager sqlTdeManager) : IEntityController<SqlTdeCertificate>
{
    public async Task<ReconciliationResult<SqlTdeCertificate>> ReconcileAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlTdeCertificate {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var cert = entity.Spec.Certificate;
        if (cert == null) return ReconciliationResult<SqlTdeCertificate>.Success(entity);

        var (host, port, password) = await ResolveTargetAsync(entity, ns, ct);
        if (host == null || password == null) return ReconciliationResult<SqlTdeCertificate>.Success(entity);

        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        // Ensure master key
        if (!await sqlTdeManager.MasterKeyExistsAsync(connection, ct))
        {
            await sqlTdeManager.CreateMasterKeyAsync(connection, password, ct);
            logger.LogInformation("Created Database Master Key on {Host}", host);
        }

        // Create certificate if not exists
        if (!await sqlTdeManager.CertificateExistsAsync(connection, cert.Name, ct))
        {
            if (cert.Source == "Generate" && cert.Generate != null)
            {
                await sqlTdeManager.CreateCertificateAsync(connection, cert.Name,
                    cert.Generate.Subject ?? $"TDE Certificate {cert.Name}",
                    cert.Generate.ExpiryDate, ct);
                logger.LogInformation("Generated TDE certificate {CertName}", cert.Name);
            }
        }

        // Encrypt databases
        var encryptedDbs = new List<EncryptedDatabaseStatus>();
        if (entity.Spec.EncryptDatabases != null)
        {
            foreach (var db in entity.Spec.EncryptDatabases.Where(d => d.Enabled))
            {
                await sqlTdeManager.EnableDatabaseEncryptionAsync(connection, db.Name, cert.Name, db.EncryptionAlgorithm, ct);
                var state = await sqlTdeManager.GetDatabaseEncryptionStateAsync(connection, db.Name, ct);
                encryptedDbs.Add(new EncryptedDatabaseStatus
                {
                    Name = db.Name,
                    EncryptionState = state,
                    EncryptionAlgorithm = db.EncryptionAlgorithm
                });
                logger.LogInformation("TDE enabled on database {Db}", db.Name);
            }
        }

        // Update status
        var thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(connection, cert.Name, ct);
        entity.Status ??= new SqlTdeCertificateStatus();
        entity.Status.CertificateCreated = true;
        entity.Status.Thumbprint = thumbprint;
        entity.Status.EncryptedDatabases = encryptedDbs;
        entity.Status.Conditions =
        [
            new ConditionStatus
            {
                Type = "Ready", Status = "True", Reason = "CertificateDeployed",
                Message = $"Certificate deployed, {encryptedDbs.Count} databases encrypted",
                LastTransitionTime = DateTime.UtcNow
            }
        ];
        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return ReconciliationResult<SqlTdeCertificate>.Success(entity);
    }

    public Task<ReconciliationResult<SqlTdeCertificate>> DeletedAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("SqlTdeCertificate {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }

    private async Task<(string? host, int port, string? password)> ResolveTargetAsync(SqlTdeCertificate entity, string ns, CancellationToken ct)
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
