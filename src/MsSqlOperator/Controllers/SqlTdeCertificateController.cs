using System.Security.Cryptography;
using System.Text;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Backup;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.Metrics;
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
    IBackupEncryptor backupEncryptor,
    OperatorMetrics metrics) : IEntityController<SqlTdeCertificate>
{
    // ── Entry Point ───────────────────────────────────────────

    public async Task<ReconciliationResult<SqlTdeCertificate>> ReconcileAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlTdeCertificate {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        entity.Status ??= new SqlTdeCertificateStatus();
        entity.Status.Conditions ??= [];
        var generation = entity.Metadata.Generation ?? 0;
        entity.Status.ObservedGeneration = generation;

        // Resolve targets
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        if (resolvedTargets.Count == 0)
        {
            entity.Status.Phase = "Pending";
            entity.Status.Targets = 0;
            entity.Status.EncryptedDatabases = 0;
            entity.Status.PendingDatabases = 0;
            entity.Status.FailedDatabases = 0;
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            metrics.SetTdeDatabases(ns, name, 0, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlTdeCertificate>.Success(entity);
        }

        // Resolve certificate version
        var version = await ResolveCertificateVersionAsync(entity, ns, ct);
        if (version == null)
        {
            entity.Status.Phase = "Failed";
            entity.Status.Targets = resolvedTargets.Count;
            SetCondition(entity, "Ready", "False", "NoSource", "Cannot resolve certificate source");
            metrics.SetTdeDatabases(ns, name, 0, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlTdeCertificate>.Success(entity);
        }

        // Age/expiry warnings (never blocks)
        EmitAgeAndExpiryWarnings(entity, version);

        // Check rotation trigger. Interval is measured from the slim-status LastRotationTime;
        // candidate detection is handled live per-database (protector thumbprint comparison), so an
        // unnecessary rotation degrades to a no-op rather than relying on a per-target status tree.
        var shouldRotate = ShouldTriggerRotation(entity);

        // MaxParallelTargets throttling
        var maxTargets = entity.Spec.Rotation?.MaxParallelTargets ?? resolvedTargets.Count;
        var targetsToProcess = resolvedTargets.Take(maxTargets).ToList();

        // Local roll-ups accumulated across all targets/databases this reconcile. These replace the
        // removed per-target/per-database status arrays — they are derived purely from live SQL truth.
        var outcome = new ReconcileOutcome
        {
            // Carry forward the persisted retirement deadline so a steady-state reconcile (no rotation
            // this pass) re-publishes the same value instead of nulling it and churning the status.
            RetireNotBefore = entity.Status?.RetireNotBefore,
        };

        foreach (var target in targetsToProcess)
        {
            try
            {
                if (target.Kind == "SqlAvailabilityGroup")
                    await ProvisionAgTdeAsync(target, entity, version, shouldRotate, outcome, ct);
                else
                    await ProvisionInstanceTdeAsync(target, entity, version, shouldRotate, outcome, ct);

                outcome.TargetsSucceeded++;
            }
            catch (Exception ex)
            {
                outcome.TargetsFailed++;
                logger.LogWarning(ex, "Error deploying TDE on {Kind}/{Name}", target.Kind, target.Name);
                await eventPublisher.PublishAsync(entity, "TargetUnreachable",
                    $"Failed deploying TDE on {target.Kind}/{target.Name}: {ex.Message}", "Warning", ct);
            }
        }

        // ── Slim status roll-up ───────────────────────────────────
        entity.Status.Targets = resolvedTargets.Count;
        entity.Status.ActiveVersion = outcome.ActiveVersion;
        entity.Status.ActiveThumbprint = outcome.ActiveThumbprint;
        entity.Status.CandidateThumbprint = outcome.IsRotating ? outcome.CandidateThumbprint : null;
        entity.Status.RotationPhase = outcome.RotationPhase;
        entity.Status.RetirementPendingApproval = outcome.RetirementPendingApproval;
        entity.Status.RetireNotBefore = outcome.RetireNotBefore;
        entity.Status.EncryptedDatabases = outcome.EncryptedDatabases;
        entity.Status.PendingDatabases = outcome.PendingDatabases;
        entity.Status.FailedDatabases = outcome.FailedDatabases;

        if (outcome.RotationCompletedThisReconcile)
        {
            entity.Status.LastRotationTime = DateTime.UtcNow;
            await eventPublisher.PublishAsync(entity, "RotationCompleted",
                $"Rotation to version {version.Version} completed on {outcome.TargetsSucceeded} target(s)", ct: ct);
        }

        // Overall phase + Ready condition.
        var anyFailure = outcome.TargetsFailed > 0 || outcome.FailedDatabases > 0;
        if (anyFailure && outcome.TargetsSucceeded == 0)
        {
            entity.Status.Phase = "Failed";
            SetCondition(entity, "Ready", "False", "Failed",
                $"TDE deployment failed on all {targetsToProcess.Count} target(s)");
            await eventPublisher.PublishAsync(entity, "RotationFailed",
                $"TDE deployment failed on all {targetsToProcess.Count} target(s)", "Warning", ct);
        }
        else if (anyFailure)
        {
            entity.Status.Phase = "Degraded";
            SetCondition(entity, "Ready", "False", "PartialError",
                $"TDE deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s); {outcome.FailedDatabases} database(s) failed");
        }
        else if (outcome.PendingDatabases > 0 || outcome.IsRotating)
        {
            entity.Status.Phase = "Provisioning";
            SetCondition(entity, "Ready", "True", "CertificateDeployed",
                $"Certificate deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s); rotation in progress");
        }
        else
        {
            entity.Status.Phase = "Ready";
            SetCondition(entity, "Ready", "True", "CertificateDeployed",
                $"Certificate deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s)");
        }

        metrics.SetTdeDatabases(ns, name, outcome.EncryptedDatabases, outcome.PendingDatabases, outcome.FailedDatabases);

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        await eventPublisher.PublishAsync(entity, "CertificateDeployed",
            $"Certificate {version.SqlCertificateName} (version {version.Version}) deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s)", ct: ct);

        // Pending databases (DEK-regen throttling) need another pass to converge.
        return outcome.PendingDatabases > 0
            ? ReconciliationResult<SqlTdeCertificate>.Success(entity, TimeSpan.FromSeconds(30))
            : ReconciliationResult<SqlTdeCertificate>.Success(entity);
    }

    public Task<ReconciliationResult<SqlTdeCertificate>> DeletedAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogInformation("SqlTdeCertificate {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlTdeCertificate", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlTdeCertificate>.Success(entity));
    }

    // ── Age/Expiry Warnings ──────────────────────────────

    private void EmitAgeAndExpiryWarnings(SqlTdeCertificate entity, CertificateVersionInfo version)
    {
        var rotation = entity.Spec.Rotation;
        if (rotation == null) return;

        // Certificate age is derived from the persisted LastRotationTime (slim status) — the only
        // age signal we keep. The previous per-target StartedAt tree is gone.
        var lastRotation = entity.Status?.LastRotationTime;
        if (lastRotation != null)
        {
            var certAge = DateTime.UtcNow - lastRotation.Value;
            if (certAge.TotalDays >= rotation.AlertAfterCertificateAgeDays)
            {
                logger.LogWarning(
                    "Certificate {LogicalName} is {AgeDays} days old (alert threshold: {ThresholdDays})",
                    entity.Spec.Certificate.LogicalName, (int)certAge.TotalDays, rotation.AlertAfterCertificateAgeDays);
                SetCondition(entity, "CertificateAge", "True", "CertificateOld",
                    $"Certificate is {(int)certAge.TotalDays} days old, threshold is {rotation.AlertAfterCertificateAgeDays}");
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

    private bool ShouldTriggerRotation(SqlTdeCertificate entity)
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
                // Candidate-vs-active is detected live per-database via the protector thumbprint
                // comparison in ProcessDatabasesAsync; ChangeDekProtector is skipped when they match,
                // so it is safe to always attempt rotation here.
                return true;

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
        // Interval is measured from the persisted slim-status LastRotationTime (the one rotation
        // timestamp we still keep) rather than a per-target Rotation.CompletedAt tree.
        var lastRotation = entity.Status?.LastRotationTime;
        if (lastRotation == null) return true; // Never rotated
        return (DateTime.UtcNow - lastRotation.Value).TotalDays >= intervalDays;
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
        CertificateVersionInfo version, bool shouldRotate, ReconcileOutcome outcome, CancellationToken ct)
    {
        outcome.NoteRotationPhase("Preparing");

        await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);

        // Master key
        if (!await sqlTdeManager.MasterKeyExistsAsync(conn, ct))
            await sqlTdeManager.CreateMasterKeyAsync(conn, target.SaPassword, ct);

        // Certificate create/import re-encrypts the private key with the DMK, which must be
        // open in this session (a DMK created in a prior reconcile is not auto-opened here).
        await sqlTdeManager.OpenMasterKeyAsync(conn, target.SaPassword, ct);

        // Certificate create/import
        var (thumbprint, password) = await EnsureCertificateOnTargetAsync(conn, entity, version, ct);

        // Candidate identity is derived live from the certificate now present on the instance.
        outcome.NoteCandidate(version, thumbprint);

        // Evidence gating (live: source secret + archive Secret + S3 object existence)
        var evidenceOk = await VerifyEvidenceBeforeRotationAsync(entity, version, conn, thumbprint, password, ct);
        if (!evidenceOk)
        {
            outcome.NoteRotationPhase("Preparing");
            logger.LogWarning("Evidence requirements not satisfied for {LogicalName}, rotation blocked",
                entity.Spec.Certificate.LogicalName);
            return;
        }

        if (!shouldRotate)
        {
            // Provisioning only (no rotation triggered): cert is the active material.
            outcome.NoteActive(version, thumbprint);
            outcome.NoteRotationPhase("Idle");
            return;
        }

        // Rotate databases
        outcome.NoteRotationPhase("Rotating");
        outcome.IsRotating = true;

        var dbOutcome = await ProcessDatabasesAsync(conn, entity, version, outcome, ct);

        // Determine final phase from live per-database results.
        if (dbOutcome.AnyWaiting)
        {
            outcome.NoteRotationPhase("RetentionWaiting");
        }
        else if (dbOutcome.AnyFailed)
        {
            outcome.NoteRotationPhase("Failed");
        }
        else if (!dbOutcome.AnyPending)
        {
            // Rotation succeeded across all selected databases on this target.
            outcome.NoteActive(version, thumbprint);
            // Only stamp completion when real rotation work happened this reconcile; otherwise this is a
            // steady-state no-op and stamping LastRotationTime/RetireNotBefore would churn the status.
            if (dbOutcome.AnyProtectorChanged)
            {
                outcome.RotationCompletedThisReconcile = true;
                var retireDays = entity.Spec.Rotation?.Retirement.NotBeforeDays ?? 90;
                outcome.RetireNotBefore = DateTime.UtcNow.AddDays(retireDays);
            }
            outcome.NoteRotationPhase("RetentionWaiting");
        }

        // Check old certificate retirement (approval gating only; live evidence already verified)
        await TryRetireOldCertificateAsync(conn, entity, outcome, ct);
    }

    // ── AG Provisioning ─────────────────────────────

    private async Task ProvisionAgTdeAsync(ResolvedTarget target, SqlTdeCertificate entity,
        CertificateVersionInfo version, bool shouldRotate, ReconcileOutcome outcome, CancellationToken ct)
    {
        outcome.NoteRotationPhase("Preparing");

        var replicaCount = target.ReplicaCount ?? 3;
        byte[]? certBytes = null;
        byte[]? keyBytes = null;
        string? thumbprint = null;
        string? password = null;

        // Deploy cert on ALL replicas before any database operation. Per-replica state is no
        // longer persisted; cert presence is verified live via CertificateExistsAsync per replica.
        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{target.Name}-{i}";
            var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);

            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);

                if (!await sqlTdeManager.MasterKeyExistsAsync(conn, ct))
                    await sqlTdeManager.CreateMasterKeyAsync(conn, target.SaPassword, ct);

                // The DMK must be open in this session to export/import a certificate's
                // private key (it is not auto-opened across sessions on SQL Server for Linux).
                await sqlTdeManager.OpenMasterKeyAsync(conn, target.SaPassword, ct);

                if (i == 0)
                {
                    (thumbprint, password) = await EnsureCertificateOnTargetAsync(conn, entity, version, ct);
                    if (string.IsNullOrEmpty(password))
                        throw new InvalidOperationException($"No encryption password resolved for TDE certificate {version.SqlCertificateName}; cannot export private key safely.");
                    // Re-export with the resolved password so every replica restores with the same key material.
                    (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct);
                }
                else if (certBytes != null && keyBytes != null && !string.IsNullOrEmpty(password))
                {
                    if (!await sqlTdeManager.CertificateExistsAsync(conn, version.SqlCertificateName, ct))
                        await sqlTdeManager.RestoreCertificateWithPasswordAsync(conn, version.SqlCertificateName, certBytes, keyBytes, password, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error deploying TDE cert on replica {Replica}", replicaName);
            }
        }

        outcome.NoteCandidate(version, thumbprint);

        // Evidence + S3 on primary
        if (thumbprint != null)
        {
            var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, replicaCount, ct);
            if (primary != null)
            {
                await using var primaryConn = primary.Value.Connection;

                var evidenceOk = await VerifyEvidenceBeforeRotationAsync(entity, version, primaryConn, thumbprint, password, ct);
                if (!evidenceOk)
                {
                    outcome.NoteRotationPhase("Preparing");
                    return;
                }

                if (!shouldRotate)
                {
                    outcome.NoteActive(version, thumbprint);
                    outcome.NoteRotationPhase("Idle");
                    return;
                }

                // Process databases on primary only
                outcome.NoteRotationPhase("Rotating");
                outcome.IsRotating = true;

                var dbOutcome = await ProcessDatabasesAsync(primaryConn, entity, version, outcome, ct);

                if (dbOutcome.AnyWaiting)
                {
                    outcome.NoteRotationPhase("RetentionWaiting");
                }
                else if (dbOutcome.AnyFailed)
                {
                    outcome.NoteRotationPhase("Failed");
                }
                else if (!dbOutcome.AnyPending)
                {
                    outcome.NoteActive(version, thumbprint);
                    if (dbOutcome.AnyProtectorChanged)
                    {
                        outcome.RotationCompletedThisReconcile = true;
                        outcome.RetireNotBefore = DateTime.UtcNow.AddDays(
                            entity.Spec.Rotation?.Retirement.NotBeforeDays ?? 90);
                    }
                    outcome.NoteRotationPhase("RetentionWaiting");
                }

                await TryRetireOldCertificateAsync(primaryConn, entity, outcome, ct);
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
            // Resolve a usable password so the cert can be re-exported/propagated safely.
            var pwd = await ResolveOrGenerateEncryptionPasswordAsync(entity, version, ct);
            return (tp, pwd);
        }

        if (version.NeedsGeneration)
        {
            await sqlTdeManager.CreateCertificateAsync(conn, version.SqlCertificateName,
                version.Subject ?? $"TDE Certificate {version.SqlCertificateName}",
                version.ExpiryDate, ct);

            var password = await ResolveOrGenerateEncryptionPasswordAsync(entity, version, ct);
            var (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct);

            // Always persist the password next to the private key so the backup is recoverable.
            await CreateSourceSecretAsync(entity, version, certBytes, keyBytes, password, ct);

            var tp = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
            return (tp, password);
        }

        // Import from existing material — a .pvk private key is always password-encrypted.
        if (version.CertBytes != null && version.KeyBytes != null)
        {
            if (string.IsNullOrEmpty(version.Password))
                throw new InvalidOperationException(
                    $"Cannot import TDE certificate '{version.SqlCertificateName}': the source secret has no private-key password. Provide the password the .pvk was exported with.");
            await sqlTdeManager.RestoreCertificateWithPasswordAsync(conn, version.SqlCertificateName, version.CertBytes, version.KeyBytes, version.Password, ct);
        }

        var thumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);
        return (thumbprint, version.Password);
    }

    // ── Evidence Gating ──────────────────────────────────
    // Evidence is no longer stored in a status array. It is re-verified live every reconcile by
    // checking the existence of the immutable Secret/S3 objects that ARE the evidence:
    //   - the source Secret (must exist),
    //   - the archive Secret (created idempotently if configured),
    //   - the S3 backup object (HEAD-checked / created idempotently if configured).

    private async Task<bool> VerifyEvidenceBeforeRotationAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, Microsoft.Data.SqlClient.SqlConnection conn,
        string? thumbprint, string? password, CancellationToken ct)
    {
        // Evidence 1: Source secret must exist
        var ns = entity.Metadata.NamespaceProperty;
        if (version.SourceSecretName != null)
        {
            var sourceSecret = await TryGetSecretAsync(ns, version.SourceSecretName, ct);
            if (sourceSecret == null)
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
            if (!archiveOk)
            {
                logger.LogWarning("Failed to create archive secret for {LogicalName}", entity.Spec.Certificate.LogicalName);
                return false;
            }
        }

        // Evidence 3: S3 backup if configured
        var s3Config = entity.Spec.Certificate.BackupToS3;
        if (s3Config != null && thumbprint != null)
        {
            var s3Ok = await BackupToS3IfConfiguredAsync(entity, version, conn, thumbprint, password, ct);
            if (!s3Ok)
            {
                logger.LogWarning("S3 backup failed for {LogicalName}, rotation blocked", entity.Spec.Certificate.LogicalName);
                return false;
            }
        }

        return true;
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

        // Get cert material. Fall back to the operator-resolved password if the archive has no
        // dedicated one, so the archived private key is always recoverable.
        var archivePassword = await GetPasswordFromSecretRefAsync(archiveSpec.EncryptionPasswordSecretRef, ns, ct)
            ?? await ResolveOrGenerateEncryptionPasswordAsync(entity, version, ct);
        var (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, archivePassword, ct);

        var data = new Dictionary<string, byte[]>
        {
            ["certificate.cer"] = certBytes,
            ["private-key.pvk"] = keyBytes,
            ["password"] = Encoding.UTF8.GetBytes(archivePassword),
        };

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
                },
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false"
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
        string? thumbprint, string? password, CancellationToken ct)
    {
        var s3Config = entity.Spec.Certificate.BackupToS3;
        if (s3Config == null || thumbprint == null) return true;

        var thumbprintShort = thumbprint.Length >= 8 ? thumbprint[..8] : thumbprint;
        var objectKey = ResolveTemplate(s3Config.ObjectKeyTemplate,
            entity.Spec.Certificate.LogicalName, version.Version, thumbprintShort,
            s3Config.Connection.ClusterName);

        var s3Conn = await BuildS3ConnectionAsync(s3Config.Connection, entity.Metadata.NamespaceProperty, ct);

        // Idempotent: HEAD check. The S3 object's existence IS the rotation evidence.
        if (await s3Client.ObjectExistsAsync(s3Conn, objectKey, ct))
        {
            logger.LogInformation("S3 backup already exists at {Key}, skipping", objectKey);
            return true;
        }

        if (string.IsNullOrEmpty(password))
        {
            logger.LogWarning("Skipping S3 backup for {Cert}: no encryption password resolved", version.SqlCertificateName);
            return false;
        }

        try
        {
            var (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct);

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

            logger.LogInformation("Certificate backed up to S3: {Key}", objectKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "S3 backup failed for {Key}", objectKey);
            return false;
        }
    }

    // ── Database Processing ──────────────────────────
    // Per-database state is read live (GetDatabaseEncryptionStateAsync / GetDekProtectorThumbprintAsync)
    // every reconcile rather than from a status snapshot. Results are folded into the local roll-up.

    private async Task<DatabaseOutcome> ProcessDatabasesAsync(Microsoft.Data.SqlClient.SqlConnection conn,
        SqlTdeCertificate entity, CertificateVersionInfo version, ReconcileOutcome outcome, CancellationToken ct)
    {
        var selector = entity.Spec.Databases.Selector;
        var policy = entity.Spec.Databases.Policy;

        var allDbs = await sqlTdeManager.GetUserDatabaseNamesAsync(conn, ct);
        var selectedDatabases = SelectDatabases(selector, allDbs);

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
        var maxParallelDbs = entity.Spec.Rotation?.DekRegeneration?.MaxParallelDatabasesPerTarget ?? selectedDatabases.Count;
        var timeoutMinutes = entity.Spec.Rotation?.DekRegeneration?.PerDatabaseTimeoutMinutes ?? 120;
        var processedDbs = 0;

        var result = new DatabaseOutcome();
        var newThumbprint = await sqlTdeManager.GetCertificateThumbprintAsync(conn, version.SqlCertificateName, ct);

        foreach (var dbName in selectedDatabases)
        {
            try
            {
                var state = await sqlTdeManager.GetDatabaseEncryptionStateAsync(conn, dbName, ct);

                if (state == null) // No DEK
                {
                    switch (policy.NonEncryptedDatabasePolicy)
                    {
                        case "Encrypt":
                            await sqlTdeManager.EnableDatabaseEncryptionAsync(conn, dbName,
                                version.SqlCertificateName, policy.EncryptionAlgorithm, ct);
                            outcome.EncryptedDatabases++;
                            result.AnyProtectorChanged = true;
                            await eventPublisher.PublishAsync(entity, "DatabaseEncrypted",
                                $"Database '{dbName}' encrypted with {version.SqlCertificateName}", ct: ct);
                            break;
                        case "Skip":
                        case "Warn":
                            if (policy.NonEncryptedDatabasePolicy == "Warn")
                                logger.LogWarning("Database '{Database}' has no DEK, skipping", dbName);
                            break;
                        case "Fail":
                            result.AnyFailed = true;
                            outcome.FailedDatabases++;
                            logger.LogWarning("Database '{Database}' has no DEK and policy is Fail", dbName);
                            break;
                    }
                }
                else
                {
                    // Change protector (live comparison: skip when already on the new cert).
                    var currentThumbprint = await sqlTdeManager.GetDekProtectorThumbprintAsync(conn, dbName, ct);

                    if (!string.Equals(currentThumbprint, newThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        await sqlTdeManager.ChangeDekProtectorAsync(conn, dbName, version.SqlCertificateName, ct);
                        result.AnyProtectorChanged = true;
                        await eventPublisher.PublishAsync(entity, "ProtectorRotated",
                            $"Database '{dbName}' DEK protector rotated to {version.SqlCertificateName}", ct: ct);
                    }

                    outcome.EncryptedDatabases++;

                    // Optional DEK regeneration
                    if (entity.Spec.Rotation?.Mode == "ProtectorAndDekRegenerate"
                        && entity.Spec.Rotation.DekRegeneration != null)
                    {
                        // Throttle: check maxParallelDatabasesPerTarget
                        if (processedDbs >= maxParallelDbs)
                        {
                            result.AnyPending = true; // Will be picked up next reconciliation
                            outcome.PendingDatabases++;
                            continue;
                        }

                        // Timeout via CancellationToken
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

                        try
                        {
                            await sqlTdeManager.RegenerateDekAsync(conn, dbName, policy.EncryptionAlgorithm, timeoutCts.Token);
                            processedDbs++;
                            await eventPublisher.PublishAsync(entity, "DekRegenerated",
                                $"Database '{dbName}' DEK regenerated", ct: ct);
                        }
                        catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Message.Contains("log backup", StringComparison.OrdinalIgnoreCase))
                        {
                            // WaitingForLogBackup
                            result.AnyWaiting = true;
                            outcome.PendingDatabases++;
                            logger.LogWarning("Database '{Database}' requires log backup before DEK regeneration", dbName);
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            result.AnyFailed = true;
                            outcome.FailedDatabases++;
                            logger.LogWarning("DEK regeneration timed out for database '{Database}'", dbName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.AnyFailed = true;
                outcome.FailedDatabases++;
                logger.LogWarning(ex, "Error processing database '{Database}'", dbName);
            }
        }

        return result;
    }

    // ── Old Certificate Retirement ───────────────────────
    // notBefore is enforced from outcome.RetireNotBefore (computed this reconcile after a successful
    // rotation). Backup evidence is the live Secret/S3 objects already verified in evidence gating, so
    // RequireFreshFullBackupAfterRotation is satisfied by reaching RetentionWaiting. The previous
    // per-target Evidences/History tree is gone — retirement here is purely approval gating.

    private async Task TryRetireOldCertificateAsync(Microsoft.Data.SqlClient.SqlConnection conn,
        SqlTdeCertificate entity, ReconcileOutcome outcome, CancellationToken ct)
    {
        var retirement = entity.Spec.Rotation?.Retirement;
        if (retirement == null) return;
        if (outcome.RotationPhase != "RetentionWaiting") return;

        // notBeforeDays check
        if (outcome.RetireNotBefore.HasValue && DateTime.UtcNow < outcome.RetireNotBefore.Value)
        {
            logger.LogInformation("Retirement not yet due (notBefore: {Date})", outcome.RetireNotBefore);
            return;
        }

        // Approval mode
        if (retirement.ApprovalMode == "Manual")
        {
            var approved = entity.Metadata.Annotations?.TryGetValue("mssql.operator/approve-retirement", out var val) == true
                && !string.IsNullOrWhiteSpace(val);
            if (!approved)
            {
                outcome.RetirementPendingApproval = true;
                logger.LogInformation("Retirement pending manual approval");
                return;
            }
        }

        // AutomaticWhenSafe / approved: notBefore elapsed and backup evidence present.
        await Task.CompletedTask; // No old-cert drop performed here (retention window managed by spec).
        outcome.NoteRotationPhase("Idle");
        outcome.RetirementPendingApproval = false;
    }

    // ── Database selection (live, status-tree-free) ───────────
    // Mirrors the previous DatabaseSelector.BuildSnapshot selection rules but returns only the sorted
    // list of selected names — the per-database phase/hash/observedGeneration snapshot is gone.

    private static List<string> SelectDatabases(DatabaseSelectorSpec selector, List<string> allDatabases)
        => DatabaseSelector.Select(selector, allDatabases);

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

    /// <summary>
    /// Resolves a non-null password used to encrypt exported TDE private keys, so every backup is
    /// recoverable. Order: (1) the operator-configured EncryptionPasswordSecretRef, (2) the password
    /// already persisted in the source secret (stable across reconciles), (3) a freshly generated
    /// strong random password (persisted alongside the key when the source secret is written).
    /// </summary>
    private async Task<string> ResolveOrGenerateEncryptionPasswordAsync(SqlTdeCertificate entity,
        CertificateVersionInfo version, CancellationToken ct)
    {
        var secretRef = version.EncryptionPasswordSecretRef;
        if (secretRef != null && !string.IsNullOrEmpty(secretRef.Name))
        {
            var configured = await GetPasswordFromSecretRefAsync(secretRef, entity.Metadata.NamespaceProperty, ct);
            if (!string.IsNullOrEmpty(configured)) return configured;
            throw new InvalidOperationException(
                $"Encryption password secret '{secretRef.Name}/{secretRef.Key}' not found — cannot export the TDE private key safely.");
        }

        if (!string.IsNullOrEmpty(version.Password))
            return version.Password;

        return Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
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
                },
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false"
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

    private void SetCondition(SqlTdeCertificate entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);

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

    /// <summary>
    /// Local, in-memory roll-up accumulated across all targets/databases during a single reconcile.
    /// Replaces the removed per-target/per-database status tree — every field is derived from live
    /// SQL truth + external objects this reconcile, never read back from prior status.
    /// </summary>
    private sealed class ReconcileOutcome
    {
        public int TargetsSucceeded;
        public int TargetsFailed;
        public int EncryptedDatabases;
        public int PendingDatabases;
        public int FailedDatabases;

        public string? ActiveVersion;
        public string? ActiveThumbprint;
        public string? CandidateThumbprint;
        public bool IsRotating;
        public bool RotationCompletedThisReconcile;
        public bool RetirementPendingApproval;
        public DateTime? RetireNotBefore;

        public string RotationPhase { get; private set; } = "Idle";

        // Coarsest phase wins across targets: Failed > Rotating > Preparing > RetentionWaiting > Idle.
        private static int Rank(string phase) => phase switch
        {
            "Failed" => 5,
            "Rotating" => 4,
            "Preparing" => 3,
            "RetentionWaiting" => 2,
            _ => 1, // Idle
        };

        public void NoteRotationPhase(string phase)
        {
            if (Rank(phase) >= Rank(RotationPhase))
                RotationPhase = phase;
        }

        public void NoteCandidate(CertificateVersionInfo version, string? thumbprint)
            => CandidateThumbprint = thumbprint;

        public void NoteActive(CertificateVersionInfo version, string? thumbprint)
        {
            ActiveVersion = version.Version;
            ActiveThumbprint = thumbprint;
        }
    }

    private sealed class DatabaseOutcome
    {
        public bool AnyFailed;
        public bool AnyPending;
        public bool AnyWaiting;
        // True only when real rotation work happened this reconcile (a DEK protector was changed or a
        // database was newly encrypted). Steady-state reconciles leave it false so LastRotationTime /
        // RetireNotBefore stay stable and the status-write dedup can break the self-reconcile loop.
        public bool AnyProtectorChanged;
    }
}
