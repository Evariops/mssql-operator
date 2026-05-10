using System.Security.Cryptography;
using System.Text;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Backup;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.S3;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlTdeCertificate), Verbs = RbacVerb.All)]
public class SqlTdeCertificateController(
    ILogger<SqlTdeCertificateController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlTdeManager sqlTdeManager,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    IS3Client s3Client,
    IBackupEncryptor backupEncryptor) : IEntityController<SqlTdeCertificate>
{
    // ── Entry Point ───────────────────────────────────────────

    public async Task<ReconciliationResult<SqlTdeCertificate>> ReconcileAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlTdeCertificate {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        entity.Status ??= new SqlTdeCertificateStatus();
        entity.Status.TargetStatuses ??= [];
        entity.Status.Conditions ??= [];

        // Resolve targets
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        if (resolvedTargets.Count == 0)
        {
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlTdeCertificate>.Success(entity);
        }

        // Resolve certificate version
        var version = await ResolveCertificateVersionAsync(entity, ns, ct);
        if (version == null)
        {
            SetCondition(entity, "Ready", "False", "NoSource", "Cannot resolve certificate source");
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlTdeCertificate>.Success(entity);
        }

        // Age/expiry warnings (never blocks)
        EmitAgeAndExpiryWarnings(entity, version);

        // Check rotation trigger
        var shouldRotate = ShouldTriggerRotation(entity, version);

        // MaxParallelTargets throttling
        var maxTargets = entity.Spec.Rotation?.MaxParallelTargets ?? resolvedTargets.Count;
        var targetsToProcess = resolvedTargets.Take(maxTargets).ToList();

        var hasError = false;
        var rotatedCount = 0;
        var failedCount = 0;

        foreach (var target in targetsToProcess)
        {
            var targetStatus = GetOrCreateTargetStatus(entity, target);
            try
            {
                if (target.Kind == "SqlAvailabilityGroup")
                    await ProvisionAgTdeAsync(target, entity, version, targetStatus, shouldRotate, ct);
                else
                    await ProvisionInstanceTdeAsync(target, entity, version, targetStatus, shouldRotate, ct);

                targetStatus.LastError = null;
                rotatedCount++;
            }
            catch (Exception ex)
            {
                targetStatus.LastError = ex.Message;
                hasError = true;
                failedCount++;
                logger.LogWarning(ex, "Error deploying TDE on {Kind}/{Name}", target.Kind, target.Name);
            }
        }

        // Update rotation history
        if (shouldRotate)
            UpdateRotationHistory(entity, version, rotatedCount, failedCount, hasError);

        // Set overall condition
        var readyStatus = hasError ? "False" : "True";
        var reason = hasError ? (failedCount == targetsToProcess.Count ? "Failed" : "PartialError") : "CertificateDeployed";
        SetCondition(entity, "Ready", readyStatus, reason,
            $"Certificate deployed on {rotatedCount}/{targetsToProcess.Count} target(s)");

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "CertificateDeployed",
            $"Certificate deployed on {rotatedCount}/{targetsToProcess.Count} target(s)", ct: ct);
        return ReconciliationResult<SqlTdeCertificate>.Success(entity);
    }

    public Task<ReconciliationResult<SqlTdeCertificate>> DeletedAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("SqlTdeCertificate {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }

    // ── Age/Expiry Warnings ──────────────────────────────

    private void EmitAgeAndExpiryWarnings(SqlTdeCertificate entity, CertificateVersionInfo version)
    {
        var rotation = entity.Spec.Rotation;
        if (rotation == null) return;

        foreach (var ts in entity.Status!.TargetStatuses!)
        {
            if (ts.ActiveVersion == null) continue;
            // Check if we can determine certificate age from status
            var existingRotation = ts.Rotation;
            if (existingRotation?.StartedAt != null)
            {
                var certAge = DateTime.UtcNow - existingRotation.StartedAt.Value;
                if (certAge.TotalDays >= rotation.AlertAfterCertificateAgeDays)
                {
                    logger.LogWarning(
                        "Certificate {LogicalName} on {Kind}/{Name} is {AgeDays} days old (alert threshold: {ThresholdDays})",
                        entity.Spec.Certificate.LogicalName, ts.Kind, ts.Name,
                        (int)certAge.TotalDays, rotation.AlertAfterCertificateAgeDays);
                    SetCondition(entity, "CertificateAge", "True", "CertificateOld",
                        $"Certificate is {(int)certAge.TotalDays} days old, threshold is {rotation.AlertAfterCertificateAgeDays}");
                }
            }
        }

        // Expiry date warning
        if (version.ExpiryDate != null && DateTime.TryParse(version.ExpiryDate, out var expiry))
        {
            if (expiry < DateTime.UtcNow)
            {
                logger.LogWarning("Certificate {LogicalName} has expired ({ExpiryDate}), continuing reconciliation",
                    entity.Spec.Certificate.LogicalName, version.ExpiryDate);
                SetCondition(entity, "CertificateExpiry", "True", "Expired",
                    $"Certificate expired on {version.ExpiryDate}");
            }
            else if (expiry < DateTime.UtcNow.AddDays(90))
            {
                logger.LogWarning("Certificate {LogicalName} expires soon ({ExpiryDate})",
                    entity.Spec.Certificate.LogicalName, version.ExpiryDate);
                SetCondition(entity, "CertificateExpiry", "True", "ExpiringSoon",
                    $"Certificate expires on {version.ExpiryDate}");
            }
        }
    }

    // ── Rotation Triggers ──────────────────────────────────

    private bool ShouldTriggerRotation(SqlTdeCertificate entity, CertificateVersionInfo version)
    {
        var rotation = entity.Spec.Rotation;
        if (rotation == null) return true; // No rotation block = provision only, always proceed

        switch (rotation.Trigger)
        {
            case "ManualOnly":
                return HasManualRotationAnnotation(entity);

            case "ManualOrInterval":
                if (HasManualRotationAnnotation(entity))
                    return true;
                return IsIntervalElapsed(entity, rotation.IntervalDays);

            case "OnSourceCandidate":
                return IsNewCandidateAvailable(entity, version);

            default:
                return true;
        }
    }

    private static bool HasManualRotationAnnotation(SqlTdeCertificate entity)
    {
        return entity.Metadata.Annotations?.TryGetValue("mssql.operator/rotate", out var val) == true
            && val == "true";
    }

    private static bool IsIntervalElapsed(SqlTdeCertificate entity, int intervalDays)
    {
        // Find the most recent rotation completion across all targets
        var latestCompletion = entity.Status?.TargetStatuses?
            .Select(ts => ts.Rotation?.CompletedAt)
            .Where(d => d.HasValue)
            .Max();

        if (latestCompletion == null) return true; // Never rotated
        return (DateTime.UtcNow - latestCompletion.Value).TotalDays >= intervalDays;
    }

    private static bool IsNewCandidateAvailable(SqlTdeCertificate entity, CertificateVersionInfo version)
    {
        // Compare resolved version with active version on first target
        var firstTarget = entity.Status?.TargetStatuses?.FirstOrDefault();
        if (firstTarget?.ActiveVersion == null) return true; // No active = must provision

        return !string.Equals(firstTarget.ActiveVersion.Version, version.Version, StringComparison.Ordinal)
            || !string.Equals(firstTarget.ActiveVersion.SqlCertificateName, version.SqlCertificateName, StringComparison.Ordinal);
    }

    // ── Source Version Resolution ────────────────────────

    private async Task<CertificateVersionInfo?> ResolveCertificateVersionAsync(
        SqlTdeCertificate entity, string ns, CancellationToken ct)
    {
        var cert = entity.Spec.Certificate;
        var source = cert.Source;

        if (source.Type == "OperatorManaged" && source.OperatorManaged != null)
        {
            var managed = source.OperatorManaged;
            var versionLabel = DateTime.UtcNow.ToString("yyyyMMdd");

            var secretName = ResolveTemplate(managed.SourceSecretNameTemplate,
                cert.LogicalName, versionLabel, null, null);
            var existingSecret = await TryGetSecretAsync(ns, secretName, ct);

            if (existingSecret != null)
            {
                return new CertificateVersionInfo
                {
                    SourceType = "OperatorManaged",
                    Version = versionLabel,
                    SqlCertificateName = ResolveTemplate(managed.SqlCertificateNameTemplate,
                        cert.LogicalName, versionLabel, null, null),
                    SourceSecretName = secretName,
                    CertBytes = existingSecret.Data.TryGetValue("certificate.cer", out var c) ? c : null,
                    KeyBytes = existingSecret.Data.TryGetValue("private-key.pvk", out var k) ? k : null,
                    Password = existingSecret.Data.TryGetValue("password", out var p)
                        ? Encoding.UTF8.GetString(p) : null,
                    Subject = ResolveTemplate(managed.SubjectTemplate, cert.LogicalName, versionLabel, null, null),
                    ExpiryDate = managed.ExpiryDate,
                    NeedsGeneration = false
                };
            }

            return new CertificateVersionInfo
            {
                SourceType = "OperatorManaged",
                Version = versionLabel,
                SqlCertificateName = ResolveTemplate(managed.SqlCertificateNameTemplate,
                    cert.LogicalName, versionLabel, null, null),
                SourceSecretName = secretName,
                Subject = ResolveTemplate(managed.SubjectTemplate, cert.LogicalName, versionLabel, null, null),
                ExpiryDate = managed.ExpiryDate,
                NeedsGeneration = true,
                EncryptionPasswordSecretRef = managed.SourceSecretEncryptionPasswordRef
            };
        }

        if (source.Type == "KubernetesSecret" && source.KubernetesSecret != null)
        {
            var k8sSource = source.KubernetesSecret;
            var discovery = k8sSource.Discovery;
            var secretNs = discovery.Namespace ?? ns;

            var secrets = await DiscoverSecretsAsync(discovery, secretNs, ct);
            if (secrets.Count == 0) return null;

            V1Secret? activeSecret = null;
            if (k8sSource.ActivationPolicy == "SecretRole")
            {
                activeSecret = secrets.FirstOrDefault(s =>
                    s.Metadata?.Labels?.TryGetValue("mssql.operator/certificate-role", out var role) == true
                    && role == "candidate")
                    ?? secrets.FirstOrDefault(s =>
                        s.Metadata?.Labels?.TryGetValue("mssql.operator/certificate-role", out var role) == true
                        && role == "active");
            }
            else // NewestReadyCandidate
            {
                activeSecret = secrets
                    .Where(s => s.Metadata?.Labels?.TryGetValue("mssql.operator/ready", out var r) == true && r == "true")
                    .OrderByDescending(s => s.Metadata?.CreationTimestamp)
                    .FirstOrDefault();
            }

            if (activeSecret?.Data == null) return null;

            var versionRef = FindVersionRef(discovery, activeSecret.Metadata.Name);
            var certKey = versionRef?.CertificateKey ?? "certificate.cer";
            var keyKey = versionRef?.PrivateKeyKey ?? "private-key.pvk";
            var pwdKey = versionRef?.PasswordKey ?? "password";

            var versionValue = activeSecret.Metadata.Labels?.TryGetValue("mssql.operator/certificate-version", out var v) == true ? v : "imported";
            var sqlName = versionRef?.SqlCertificateName
                ?? (activeSecret.Metadata.Annotations?.TryGetValue("mssql.operator/sql-certificate-name", out var sn) == true ? sn : null)
                ?? $"{cert.LogicalName}_{versionValue}";

            return new CertificateVersionInfo
            {
                SourceType = "KubernetesSecret",
                Version = versionValue,
                SqlCertificateName = sqlName,
                SourceSecretName = activeSecret.Metadata.Name,
                CertBytes = activeSecret.Data.TryGetValue(certKey, out var cb) ? cb : null,
                KeyBytes = activeSecret.Data.TryGetValue(keyKey, out var kb) ? kb : null,
                Password = activeSecret.Data.TryGetValue(pwdKey, out var pb)
                    ? Encoding.UTF8.GetString(pb) : null,
                NeedsGeneration = false
            };
        }

        return null;
    }

    // ── Instance Provisioning ────────────────────────

    private async Task ProvisionInstanceTdeAsync(ResolvedTarget target, SqlTdeCertificate entity,
        CertificateVersionInfo version, TdeCertificateTargetStatus targetStatus,
        bool shouldRotate, CancellationToken ct)
    {
        // Set phase
        targetStatus.Rotation ??= new RotationStatus();
        targetStatus.Rotation.Phase = "Preparing";
        targetStatus.Rotation.Mode = entity.Spec.Rotation?.Mode ?? "ProtectorOnly";

        await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);

        // Master key
        if (!await sqlTdeManager.MasterKeyExistsAsync(conn, ct))
            await sqlTdeManager.CreateMasterKeyAsync(conn, target.SaPassword, ct);

        // Certificate create/import
        var (thumbprint, password) = await EnsureCertificateOnTargetAsync(conn, entity, version, ct);

        // Track candidate before promotion
        targetStatus.CandidateVersion = new CertificateVersionStatus
        {
            SourceType = version.SourceType,
            Version = version.Version,
            SqlCertificateName = version.SqlCertificateName,
            Thumbprint = thumbprint ?? string.Empty,
            SourceSecretName = version.SourceSecretName
        };

        // Evidence gating
        var evidenceOk = await VerifyEvidenceBeforeRotationAsync(entity, version, conn, thumbprint, password, targetStatus, ct);
        if (!evidenceOk)
        {
            targetStatus.Rotation.Phase = "Preparing";
            targetStatus.Rotation.LastError = "Evidence requirements not satisfied, rotation blocked";
            logger.LogWarning("Evidence requirements not satisfied for {LogicalName}, rotation blocked",
                entity.Spec.Certificate.LogicalName);
            return;
        }

        if (!shouldRotate)
        {
            // Provisioning only (no rotation triggered)
            targetStatus.ActiveVersion = targetStatus.CandidateVersion;
            targetStatus.CandidateVersion = null;
            targetStatus.Rotation.Phase = "Idle";
            return;
        }

        // Rotate databases
        targetStatus.Rotation.Phase = "Rotating";
        targetStatus.Rotation.StartedAt ??= DateTime.UtcNow;
        targetStatus.Rotation.Attempt++;

        await ProcessDatabasesAsync(conn, entity, version, targetStatus, ct);

        // Determine final phase
        var dbSnapshot = targetStatus.DatabaseSelection;
        if (dbSnapshot != null)
        {
            var anyFailed = dbSnapshot.Databases.Any(d => d.Phase == "Failed");
            var anyPending = dbSnapshot.Databases.Any(d => d.Phase == "Pending");
            var anyWaiting = dbSnapshot.Databases.Any(d => d.Phase == "WaitingForLogBackup");

            if (anyWaiting)
            {
                targetStatus.Rotation.Phase = "WaitingForLogBackup";
            }
            else if (anyFailed && !anyPending)
            {
                targetStatus.Rotation.Phase = "PartiallyCompleted";
            }
            else if (!anyFailed && !anyPending)
            {
                // Retirement setup
                targetStatus.Rotation.Phase = "RetentionWaiting";
                targetStatus.Rotation.CompletedAt = DateTime.UtcNow;
                var retireDays = entity.Spec.Rotation?.Retirement.NotBeforeDays ?? 90;
                targetStatus.Rotation.RetireNotBefore = DateTime.UtcNow.AddDays(retireDays);

                // Promote candidate to active
                targetStatus.ActiveVersion = targetStatus.CandidateVersion;
                targetStatus.CandidateVersion = null;
            }
            else if (anyFailed)
            {
                targetStatus.Rotation.Phase = "PartiallyCompleted";
            }
        }

        // Check old certificate retirement
        await TryRetireOldCertificateAsync(conn, entity, targetStatus, ct);
    }

    // ── AG Provisioning ─────────────────────────────

    private async Task ProvisionAgTdeAsync(ResolvedTarget target, SqlTdeCertificate entity,
        CertificateVersionInfo version, TdeCertificateTargetStatus targetStatus,
        bool shouldRotate, CancellationToken ct)
    {
        targetStatus.Rotation ??= new RotationStatus();
        targetStatus.Rotation.Phase = "Preparing";
        targetStatus.Rotation.Mode = entity.Spec.Rotation?.Mode ?? "ProtectorOnly";

        var replicaCount = target.ReplicaCount ?? 3;
        targetStatus.ReplicaStatuses ??= [];
        byte[]? certBytes = null;
        byte[]? keyBytes = null;
        string? thumbprint = null;
        string? password = null;

        // Deploy cert on ALL replicas before any database operation
        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{target.Name}-{i}";
            var host = $"{replicaName}.{target.Namespace}.svc.cluster.local";
            var replicaStatus = targetStatus.ReplicaStatuses.FirstOrDefault(r => r.Replica == replicaName)
                ?? new TdeReplicaStatus { Replica = replicaName };

            if (!targetStatus.ReplicaStatuses.Contains(replicaStatus))
                targetStatus.ReplicaStatuses.Add(replicaStatus);

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
                    (thumbprint, password) = await EnsureCertificateOnTargetAsync(conn, entity, version, ct);
                    (certBytes, keyBytes) = password != null
                        ? await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct)
                        : await sqlTdeManager.BackupCertificateAsync(conn, version.SqlCertificateName, ct);
                }
                else if (certBytes != null && keyBytes != null)
                {
                    if (!await sqlTdeManager.CertificateExistsAsync(conn, version.SqlCertificateName, ct))
                    {
                        if (password != null)
                            await sqlTdeManager.RestoreCertificateWithPasswordAsync(conn, version.SqlCertificateName, certBytes, keyBytes, password, ct);
                        else
                            await sqlTdeManager.RestoreCertificateAsync(conn, version.SqlCertificateName, certBytes, keyBytes, ct);
                    }
                }

                replicaStatus.CertificateCreated = true;
                replicaStatus.Thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
                replicaStatus.LastError = null;
            }
            catch (Exception ex)
            {
                replicaStatus.LastError = ex.Message;
                logger.LogWarning(ex, "Error deploying TDE cert on replica {Replica}", replicaName);
            }
        }

        targetStatus.CandidateVersion = new CertificateVersionStatus
        {
            SourceType = version.SourceType,
            Version = version.Version,
            SqlCertificateName = version.SqlCertificateName,
            Thumbprint = thumbprint ?? string.Empty,
            SourceSecretName = version.SourceSecretName
        };

        // Evidence + S3 on primary
        if (thumbprint != null)
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, replicaCount, ct);
            if (primary != null)
            {
                await using var primaryConn = primary.Value.Connection;

                var evidenceOk = await VerifyEvidenceBeforeRotationAsync(entity, version, primaryConn, thumbprint, password, targetStatus, ct);
                if (!evidenceOk)
                {
                    targetStatus.Rotation.Phase = "Preparing";
                    targetStatus.Rotation.LastError = "Evidence requirements not satisfied";
                    return;
                }

                if (!shouldRotate)
                {
                    targetStatus.ActiveVersion = targetStatus.CandidateVersion;
                    targetStatus.CandidateVersion = null;
                    targetStatus.Rotation.Phase = "Idle";
                    return;
                }

                // Process databases on primary only
                targetStatus.Rotation.Phase = "Rotating";
                targetStatus.Rotation.StartedAt ??= DateTime.UtcNow;
                targetStatus.Rotation.Attempt++;

                await ProcessDatabasesAsync(primaryConn, entity, version, targetStatus, ct);

                // Determine phase
                var dbSnapshot = targetStatus.DatabaseSelection;
                if (dbSnapshot != null)
                {
                    var anyFailed = dbSnapshot.Databases.Any(d => d.Phase == "Failed");
                    var anyPending = dbSnapshot.Databases.Any(d => d.Phase == "Pending");
                    var anyWaiting = dbSnapshot.Databases.Any(d => d.Phase == "WaitingForLogBackup");

                    if (anyWaiting)
                        targetStatus.Rotation.Phase = "WaitingForLogBackup";
                    else if (!anyFailed && !anyPending)
                    {
                        targetStatus.Rotation.Phase = "RetentionWaiting";
                        targetStatus.Rotation.CompletedAt = DateTime.UtcNow;
                        targetStatus.Rotation.RetireNotBefore = DateTime.UtcNow.AddDays(
                            entity.Spec.Rotation?.Retirement.NotBeforeDays ?? 90);
                        targetStatus.ActiveVersion = targetStatus.CandidateVersion;
                        targetStatus.CandidateVersion = null;
                    }
                    else
                        targetStatus.Rotation.Phase = "PartiallyCompleted";
                }

                await TryRetireOldCertificateAsync(primaryConn, entity, targetStatus, ct);
            }
        }
    }

    // ── Certificate Install ──────────────────────────────

    private async Task<(string? Thumbprint, string? Password)> EnsureCertificateOnTargetAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, SqlTdeCertificate entity,
        CertificateVersionInfo version, CancellationToken ct)
    {
        if (await sqlTdeManager.CertificateExistsAsync(conn, version.SqlCertificateName, ct))
        {
            var tp = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
            return (tp, version.Password);
        }

        if (version.NeedsGeneration)
        {
            await sqlTdeManager.CreateCertificateAsync(conn, version.SqlCertificateName,
                version.Subject ?? $"TDE Certificate {version.SqlCertificateName}",
                version.ExpiryDate, ct);

            var password = await GetEncryptionPasswordAsync(entity, version, ct);
            var (certBytes, keyBytes) = password != null
                ? await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct)
                : await sqlTdeManager.BackupCertificateAsync(conn, version.SqlCertificateName, ct);

            await CreateSourceSecretAsync(entity, version, certBytes, keyBytes, password, ct);

            var tp = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
            return (tp, password);
        }

        // Import from existing material
        if (version.CertBytes != null && version.KeyBytes != null)
        {
            if (version.Password != null)
                await sqlTdeManager.RestoreCertificateWithPasswordAsync(conn, version.SqlCertificateName, version.CertBytes, version.KeyBytes, version.Password, ct);
            else
                await sqlTdeManager.RestoreCertificateAsync(conn, version.SqlCertificateName, version.CertBytes, version.KeyBytes, ct);
        }

        var thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
        return (thumbprint, version.Password);
    }

    // ── Evidence Gating ──────────────────────────────────

    private async Task<bool> VerifyEvidenceBeforeRotationAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, Microsoft.Data.SqlClient.SqlConnection conn,
        string? thumbprint, string? password,
        TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        targetStatus.Rotation ??= new RotationStatus();
        targetStatus.Rotation.Evidences ??= [];

        // Evidence 1: Source secret must exist
        var ns = entity.Metadata.NamespaceProperty;
        if (version.SourceSecretName != null)
        {
            var sourceSecret = await TryGetSecretAsync(ns, version.SourceSecretName, ct);
            if (sourceSecret != null)
            {
                RecordEvidence(targetStatus, "SourceSecret", thumbprint, ns, version.SourceSecretName, version.Version);
            }
            else
            {
                logger.LogWarning("Source secret {Name} not found", version.SourceSecretName);
                return false;
            }
        }

        // Evidence 2: Archive secret if configured
        var archiveConfig = entity.Spec.Certificate.ArchiveToSecret;
        if (archiveConfig != null && thumbprint != null)
        {
            var thumbShort = thumbprint.Length >= 8 ? thumbprint[..8] : thumbprint;
            var archiveOk = await CreateArchiveSecretAsync(entity, version, conn, thumbprint, thumbShort, password, ct);
            if (archiveOk)
            {
                RecordEvidence(targetStatus, "ArchiveSecret", thumbprint, ns, null, version.Version);
            }
            else
            {
                logger.LogWarning("Failed to create archive secret for {LogicalName}", entity.Spec.Certificate.LogicalName);
                return false;
            }
        }

        // Evidence 3: S3 backup if configured
        var s3Config = entity.Spec.Certificate.BackupToS3;
        if (s3Config != null && thumbprint != null)
        {
            var s3Ok = await BackupToS3IfConfiguredAsync(entity, version, conn, thumbprint, password, targetStatus, ct);
            if (!s3Ok)
            {
                logger.LogWarning("S3 backup failed for {LogicalName}, rotation blocked", entity.Spec.Certificate.LogicalName);
                return false;
            }
        }

        return true;
    }

    private static void RecordEvidence(TdeCertificateTargetStatus targetStatus, string type,
        string? thumbprint, string? ns = null, string? name = null, string? version = null)
    {
        targetStatus.Rotation!.Evidences ??= [];
        if (targetStatus.Rotation.Evidences.Any(e => e.Type == type && e.Thumbprint == thumbprint))
            return;

        targetStatus.Rotation.Evidences.Add(new CertificateEvidenceStatus
        {
            Type = type,
            Namespace = ns,
            Name = name,
            Version = version,
            Thumbprint = thumbprint ?? string.Empty,
            ObservedAt = DateTime.UtcNow,
            Verified = true,
            Immutable = type is "ArchiveSecret" or "S3Backup"
        });
    }

    // ── ArchiveToSecret ──────────────────────────────────

    private async Task<bool> CreateArchiveSecretAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, Microsoft.Data.SqlClient.SqlConnection conn,
        string thumbprint, string thumbShort, string? password, CancellationToken ct)
    {
        var archiveSpec = entity.Spec.Certificate.ArchiveToSecret;
        if (archiveSpec == null) return true;

        var ns = entity.Metadata.NamespaceProperty;
        var secretName = ResolveTemplate(archiveSpec.SecretNameTemplate,
            entity.Spec.Certificate.LogicalName, version.Version, thumbShort, null);

        // Idempotent: check if archive already exists
        var existing = await TryGetSecretAsync(ns, secretName, ct);
        if (existing != null) return true;

        // Get cert material
        var archivePassword = await GetPasswordFromSecretRefAsync(archiveSpec.EncryptionPasswordSecretRef, ns, ct);
        var (certBytes, keyBytes) = archivePassword != null
            ? await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, archivePassword, ct)
            : await sqlTdeManager.BackupCertificateAsync(conn, version.SqlCertificateName, ct);

        var data = new Dictionary<string, byte[]>
        {
            ["certificate.cer"] = certBytes,
            ["private-key.pvk"] = keyBytes,
        };
        if (archivePassword != null)
            data["password"] = Encoding.UTF8.GetBytes(archivePassword);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["mssql.operator/certificate-logical-name"] = entity.Spec.Certificate.LogicalName,
                    ["mssql.operator/certificate-version"] = version.Version,
                    ["mssql.operator/certificate-thumbprint-short"] = thumbShort,
                    ["app.kubernetes.io/managed-by"] = "mssql-operator"
                }
            },
            Immutable = archiveSpec.Immutable,
            Type = "Opaque",
            Data = data
        };

        try
        {
            await kubernetesClient.CreateAsync(secret, ct);
            logger.LogInformation("Created archive secret {Name}", secretName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create archive secret {Name}", secretName);
            return false;
        }
    }

    // ── S3 Backup ───────────────────────────────────────

    private async Task<bool> BackupToS3IfConfiguredAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, Microsoft.Data.SqlClient.SqlConnection conn,
        string? thumbprint, string? password,
        TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        var s3Config = entity.Spec.Certificate.BackupToS3;
        if (s3Config == null || thumbprint == null) return true;

        var thumbprintShort = thumbprint.Length >= 8 ? thumbprint[..8] : thumbprint;
        var objectKey = ResolveTemplate(s3Config.ObjectKeyTemplate,
            entity.Spec.Certificate.LogicalName, version.Version, thumbprintShort,
            s3Config.Connection.ClusterName);

        var s3Conn = await BuildS3ConnectionAsync(s3Config.Connection, entity.Metadata.NamespaceProperty, ct);

        // Idempotent: HEAD check
        if (await s3Client.ObjectExistsAsync(s3Conn, objectKey, ct))
        {
            logger.LogInformation("S3 backup already exists at {Key}, skipping", objectKey);
            RecordS3Evidence(targetStatus, objectKey, thumbprint);
            return true;
        }

        try
        {
            var (certBytes, keyBytes) = password != null
                ? await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct)
                : await sqlTdeManager.BackupCertificateAsync(conn, version.SqlCertificateName, ct);

            var bundle = new BackupBundle
            {
                LogicalName = entity.Spec.Certificate.LogicalName,
                Version = version.Version,
                SqlCertificateName = version.SqlCertificateName,
                Thumbprint = thumbprint,
                Certificate = Convert.ToBase64String(certBytes),
                PrivateKey = Convert.ToBase64String(keyBytes),
                PrivateKeyPassword = password ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };

            var plaintext = bundle.Serialize();
            var encryptedBytes = await EncryptBackup(s3Config.Encryption, plaintext, entity.Metadata.NamespaceProperty, ct);

            await s3Client.PutObjectAsync(s3Conn, objectKey, encryptedBytes, ct);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(encryptedBytes));

            logger.LogInformation("Certificate backed up to S3: {Key}", objectKey);
            RecordS3Evidence(targetStatus, objectKey, thumbprint, sha256);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "S3 backup failed for {Key}", objectKey);
            return false;
        }
    }

    private static void RecordS3Evidence(TdeCertificateTargetStatus targetStatus, string objectKey, string thumbprint, string? sha256 = null)
    {
        targetStatus.Rotation ??= new RotationStatus();
        targetStatus.Rotation.Evidences ??= [];
        if (targetStatus.Rotation.Evidences.Any(e => e.Type == "S3Backup" && e.S3ObjectKey == objectKey))
            return;
        targetStatus.Rotation.Evidences.Add(new CertificateEvidenceStatus
        {
            Type = "S3Backup",
            S3ObjectKey = objectKey,
            Thumbprint = thumbprint,
            Sha256 = sha256,
            ObservedAt = DateTime.UtcNow,
            Verified = true,
            Immutable = true
        });
    }

    // ── Database Processing ──────────────────────────

    private async Task ProcessDatabasesAsync(Microsoft.Data.SqlClient.SqlConnection conn,
        SqlTdeCertificate entity, CertificateVersionInfo version,
        TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        var selector = entity.Spec.Databases.Selector;
        var policy = entity.Spec.Databases.Policy;
        var generation = entity.Metadata.Generation ?? 0;

        var allDbs = await sqlTdeManager.GetUserDatabaseNamesAsync(conn, ct);
        var snapshot = DatabaseSelector.BuildSnapshot(selector, allDbs, generation);

        // Warn about missing explicit names
        var missing = DatabaseSelector.GetMissingExplicitNames(selector, allDbs);
        foreach (var name in missing)
        {
            switch (selector.MissingDatabasePolicy)
            {
                case "Fail":
                    throw new InvalidOperationException($"Database '{name}' not found on target");
                case "Warn":
                    logger.LogWarning("Database '{Database}' specified in selector but not found on target", name);
                    break;
                    // Ignore: do nothing
            }
        }

        // MaxParallelDatabasesPerTarget throttling
        var maxParallelDbs = entity.Spec.Rotation?.DekRegeneration?.MaxParallelDatabasesPerTarget ?? snapshot.Databases.Count;
        var timeoutMinutes = entity.Spec.Rotation?.DekRegeneration?.PerDatabaseTimeoutMinutes ?? 120;
        var processedDbs = 0;

        foreach (var dbStatus in snapshot.Databases)
        {
            try
            {
                var state = await sqlTdeManager.GetDatabaseEncryptionStateAsync(conn, dbStatus.Name, ct);
                dbStatus.EncryptionState = state;

                if (state == null) // No DEK
                {
                    switch (policy.NonEncryptedDatabasePolicy)
                    {
                        case "Encrypt":
                            await sqlTdeManager.EnableDatabaseEncryptionAsync(conn, dbStatus.Name,
                                version.SqlCertificateName, policy.EncryptionAlgorithm, ct);
                            dbStatus.Phase = "ProtectorRotated";
                            dbStatus.EncryptionState = await sqlTdeManager.GetDatabaseEncryptionStateAsync(conn, dbStatus.Name, ct);
                            break;
                        case "Skip":
                            dbStatus.Phase = "Skipped";
                            break;
                        case "Warn":
                            dbStatus.Phase = "Skipped";
                            logger.LogWarning("Database '{Database}' has no DEK, skipping", dbStatus.Name);
                            break;
                        case "Fail":
                            dbStatus.Phase = "Failed";
                            dbStatus.LastError = "Database has no DEK and policy is Fail";
                            break;
                    }
                }
                else
                {
                    // Change protector
                    var currentThumbprint = await sqlTdeManager.GetDekProtectorThumbprintAsync(conn, dbStatus.Name, ct);
                    var newThumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);

                    if (!string.Equals(currentThumbprint, newThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        await sqlTdeManager.ChangeDekProtectorAsync(conn, dbStatus.Name, version.SqlCertificateName, ct);
                    }

                    dbStatus.Phase = "ProtectorRotated";
                    dbStatus.ProtectorThumbprint = newThumbprint;

                    // Optional DEK regeneration
                    if (entity.Spec.Rotation?.Mode == "ProtectorAndDekRegenerate"
                        && entity.Spec.Rotation.DekRegeneration != null)
                    {
                        // Throttle: check maxParallelDatabasesPerTarget
                        if (processedDbs >= maxParallelDbs)
                        {
                            dbStatus.Phase = "Pending"; // Will be picked up next reconciliation
                            continue;
                        }

                        // Timeout via CancellationToken
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

                        try
                        {
                            await sqlTdeManager.RegenerateDekAsync(conn, dbStatus.Name, policy.EncryptionAlgorithm, timeoutCts.Token);
                            dbStatus.Phase = "DekRegenerated";
                            processedDbs++;
                        }
                        catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Message.Contains("log backup", StringComparison.OrdinalIgnoreCase))
                        {
                            // WaitingForLogBackup
                            dbStatus.Phase = "WaitingForLogBackup";
                            dbStatus.LastError = "SQL Server requires a log backup before further DEK modification";
                            logger.LogWarning("Database '{Database}' requires log backup before DEK regeneration", dbStatus.Name);
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            dbStatus.Phase = "Failed";
                            dbStatus.LastError = $"DEK regeneration timed out after {timeoutMinutes} minutes";
                            logger.LogWarning("DEK regeneration timed out for database '{Database}'", dbStatus.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                dbStatus.Phase = "Failed";
                dbStatus.LastError = ex.Message;
                logger.LogWarning(ex, "Error processing database '{Database}'", dbStatus.Name);
            }
        }

        targetStatus.DatabaseSelection = snapshot;
    }

    // ── Old Certificate Retirement ───────────────────────

    private async Task TryRetireOldCertificateAsync(Microsoft.Data.SqlClient.SqlConnection conn,
        SqlTdeCertificate entity, TdeCertificateTargetStatus targetStatus, CancellationToken ct)
    {
        var retirement = entity.Spec.Rotation?.Retirement;
        if (retirement == null) return;
        if (targetStatus.Rotation?.Phase != "RetentionWaiting") return;

        // notBeforeDays check
        if (targetStatus.Rotation.RetireNotBefore.HasValue && DateTime.UtcNow < targetStatus.Rotation.RetireNotBefore.Value)
        {
            logger.LogInformation("Retirement not yet due (notBefore: {Date})", targetStatus.Rotation.RetireNotBefore);
            return;
        }

        // Verify backup evidence
        if (retirement.RequireFreshFullBackupAfterRotation)
        {
            var hasBackupEvidence = targetStatus.Rotation.Evidences?.Any(e =>
                e.Type is "S3Backup" or "ArchiveSecret" && e.Verified) == true;
            if (!hasBackupEvidence)
            {
                targetStatus.Rotation.RetirementBlockedByBackupEvidence = true;
                logger.LogWarning("Retirement blocked: no verified backup evidence");
                return;
            }
        }

        // Approval mode
        if (retirement.ApprovalMode == "Manual")
        {
            var approved = entity.Metadata.Annotations?.TryGetValue("mssql.operator/approve-retirement", out var val) == true
                && !string.IsNullOrWhiteSpace(val);
            if (!approved)
            {
                targetStatus.Rotation.RetirementPendingApproval = true;
                logger.LogInformation("Retirement pending manual approval");
                return;
            }
        }

        // AutomaticWhenSafe: all evidence verified, notBefore elapsed
        // At this point we can retire
        // Find old certificate names from history or previous active
        var history = targetStatus.Rotation.History;
        if (history is { Count: > 0 })
        {
            var previousVersion = history.LastOrDefault(h => h.Result == "Completed");
            if (previousVersion != null)
            {
                // The old cert name would be based on version — we only drop if
                // it exists and no databases reference it
                logger.LogInformation("Old certificate retirement eligible for version {Version}", previousVersion.Version);
            }
        }

        targetStatus.Rotation.Phase = "Idle";
        targetStatus.Rotation.RetirementPendingApproval = false;
        targetStatus.Rotation.RetirementBlockedByBackupEvidence = false;
    }

    // ── Rotation History ─────────────────────────────────

    private static void UpdateRotationHistory(SqlTdeCertificate entity, CertificateVersionInfo version,
        int rotatedCount, int failedCount, bool hasError)
    {
        foreach (var ts in entity.Status!.TargetStatuses!)
        {
            ts.Rotation ??= new RotationStatus();
            ts.Rotation.History ??= [];

            var result = hasError
                ? (failedCount > 0 && rotatedCount > 0 ? "PartiallyCompleted" : "Failed")
                : "Completed";

            ts.Rotation.History.Add(new RotationHistoryEntry
            {
                Version = version.Version,
                Mode = entity.Spec.Rotation?.Mode ?? "ProtectorOnly",
                StartedAt = ts.Rotation.StartedAt ?? DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Result = result,
                DatabasesRotated = ts.DatabaseSelection?.Databases.Count(d => d.Phase is "ProtectorRotated" or "DekRegenerated") ?? 0,
                DatabasesFailed = ts.DatabaseSelection?.Databases.Count(d => d.Phase == "Failed") ?? 0,
                Error = hasError ? ts.LastError : null
            });
        }
    }

    // ── Helpers ────────────────────────────────────────────────

    private async Task<byte[]> EncryptBackup(BackupEncryptionSpec encryption,
        byte[] plaintext, string ns, CancellationToken ct)
    {
        if (encryption.Type == "EnvelopeEncryption" && encryption.EnvelopeEncryption != null)
        {
            var pubKeySecret = await kubernetesClient.GetAsync<V1Secret>(
                encryption.EnvelopeEncryption.PublicKeySecretRef.Name, ns, ct);
            if (pubKeySecret?.Data == null)
                throw new InvalidOperationException("Public key secret not found");
            var pubKey = pubKeySecret.Data[encryption.EnvelopeEncryption.PublicKeySecretRef.Key];
            return backupEncryptor.EncryptWithEnvelope(plaintext, pubKey);
        }

        var keyRef = encryption.AesKey?.KeySecretRef ?? throw new InvalidOperationException("AES key secret ref required");
        var keySecret = await kubernetesClient.GetAsync<V1Secret>(keyRef.Name, ns, ct);
        if (keySecret?.Data == null)
            throw new InvalidOperationException("AES key secret not found");
        var aesKey = keySecret.Data[keyRef.Key];
        return backupEncryptor.EncryptWithAesKey(plaintext, aesKey);
    }

    private async Task<string?> GetEncryptionPasswordAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, CancellationToken ct)
    {
        var secretRef = version.EncryptionPasswordSecretRef;
        if (secretRef == null || string.IsNullOrEmpty(secretRef.Name)) return null;
        return await GetPasswordFromSecretRefAsync(secretRef, entity.Metadata.NamespaceProperty, ct);
    }

    private async Task<string?> GetPasswordFromSecretRefAsync(SecretReference secretRef, string ns, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secretRef.Name)) return null;
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
        if (secret?.Data == null) return null;
        return secret.Data.TryGetValue(secretRef.Key, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;
    }

    private async Task CreateSourceSecretAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, byte[] certBytes, byte[] keyBytes,
        string? password, CancellationToken ct)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var data = new Dictionary<string, byte[]>
        {
            ["certificate.cer"] = certBytes,
            ["private-key.pvk"] = keyBytes,
        };
        if (password != null)
            data["password"] = Encoding.UTF8.GetBytes(password);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = version.SourceSecretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["mssql.operator/certificate-logical-name"] = entity.Spec.Certificate.LogicalName,
                    ["mssql.operator/certificate-version"] = version.Version,
                    ["mssql.operator/certificate-role"] = "active",
                    ["mssql.operator/ready"] = "true",
                    ["app.kubernetes.io/managed-by"] = "mssql-operator"
                }
            },
            Type = "Opaque",
            Data = data
        };

        await kubernetesClient.CreateAsync(secret, ct);
        logger.LogInformation("Created source secret {Name} for certificate {LogicalName}",
            version.SourceSecretName, entity.Spec.Certificate.LogicalName);
    }

    private async Task<S3ConnectionInfo> BuildS3ConnectionAsync(S3ConnectionSpec spec, string ns, CancellationToken ct)
    {
        var credsSecret = await kubernetesClient.GetAsync<V1Secret>(spec.CredentialsSecretRef.Name, ns, ct)
            ?? throw new InvalidOperationException($"S3 credentials secret '{spec.CredentialsSecretRef.Name}' not found");

        var accessKey = credsSecret.Data.TryGetValue("accessKeyId", out var ak)
            ? Encoding.UTF8.GetString(ak)
            : throw new InvalidOperationException("accessKeyId not found in S3 credentials secret");
        var secretKey = credsSecret.Data.TryGetValue("secretAccessKey", out var sk)
            ? Encoding.UTF8.GetString(sk)
            : throw new InvalidOperationException("secretAccessKey not found in S3 credentials secret");

        return new S3ConnectionInfo(spec.Endpoint, spec.Bucket, spec.Region, accessKey, secretKey, spec.ForcePathStyle);
    }

    private async Task<List<V1Secret>> DiscoverSecretsAsync(CertificateSecretDiscoverySpec discovery, string ns, CancellationToken ct)
    {
        if (discovery.Names is { Count: > 0 })
        {
            var secrets = new List<V1Secret>();
            foreach (var nameRef in discovery.Names)
            {
                var secret = await TryGetSecretAsync(ns, nameRef.SecretName, ct);
                if (secret != null) secrets.Add(secret);
            }
            return secrets;
        }

        if (discovery.MatchLabels is { Count: > 0 })
        {
            var labelSelector = string.Join(",", discovery.MatchLabels.Select(kv => $"{kv.Key}={kv.Value}"));
            var list = await kubernetesClient.ListAsync<V1Secret>(ns, labelSelector, ct);
            return list.ToList();
        }

        return [];
    }

    private async Task<V1Secret?> TryGetSecretAsync(string ns, string name, CancellationToken ct)
    {
        try
        {
            return await kubernetesClient.GetAsync<V1Secret>(name, ns, ct);
        }
        catch
        {
            return null;
        }
    }

    private static CertificateSecretVersionRef? FindVersionRef(CertificateSecretDiscoverySpec discovery, string secretName)
    {
        return discovery.Names?.FirstOrDefault(n =>
            string.Equals(n.SecretName, secretName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTemplate(string template, string logicalName, string version,
        string? thumbprintShort, string? clusterName)
    {
        return template
            .Replace("{logicalName}", logicalName)
            .Replace("{version}", version)
            .Replace("{thumbprintShort}", thumbprintShort ?? "")
            .Replace("{timestamp}", DateTime.UtcNow.ToString("yyyyMMddHHmmss"))
            .Replace("{clusterName}", clusterName ?? "");
    }

    private static TdeCertificateTargetStatus GetOrCreateTargetStatus(SqlTdeCertificate entity, ResolvedTarget target)
    {
        var existing = entity.Status!.TargetStatuses!.FirstOrDefault(t =>
            t.Kind == target.Kind && t.Name == target.Name && t.Namespace == target.Namespace);
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
            existing.Status = status;
            existing.Reason = reason;
            existing.Message = message;
            existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            entity.Status.Conditions.Add(new ConditionStatus
            {
                Type = type, Status = status, Reason = reason, Message = message, LastTransitionTime = DateTime.UtcNow
            });
        }
    }

    private class CertificateVersionInfo
    {
        public string SourceType { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string SqlCertificateName { get; set; } = string.Empty;
        public string? SourceSecretName { get; set; }
        public byte[]? CertBytes { get; set; }
        public byte[]? KeyBytes { get; set; }
        public string? Password { get; set; }
        public string? Subject { get; set; }
        public string? ExpiryDate { get; set; }
        public bool NeedsGeneration { get; set; }
        public SecretReference? EncryptionPasswordSecretRef { get; set; }
    }
}
