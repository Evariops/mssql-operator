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
    OperatorMetrics metrics,
    TimeProvider? timeProvider = null) : IEntityController<SqlTdeCertificate>
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // ── Entry Point ───────────────────────────────────────────

    public async Task<ReconciliationResult<SqlTdeCertificate>> ReconcileAsync(SqlTdeCertificate entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlTdeCertificate {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;
        var previousPhase = entity.Status?.Phase;
        entity.Status ??= new SqlTdeCertificateStatus();
        entity.Status.Conditions ??= [];
        var generation = entity.Metadata.Generation ?? 0;
        entity.Status.ObservedGeneration = generation;

        // Resolve targets
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        if (resolvedTargets.Count == 0)
        {
            entity.Status.Phase = Phases.Pending;
            entity.Status.Targets = 0;
            entity.Status.EncryptedDatabases = 0;
            entity.Status.PendingDatabases = 0;
            entity.Status.FailedDatabases = 0;
            SetCondition(entity, "Ready", "False", "NoMatch", "No targets matched the selector");
            metrics.SetTdeDatabases(ns, name, 0, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            // Keep polling until a target appears.
            return ReconciliationResult<SqlTdeCertificate>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // Resolve certificate version
        var (version, sourceReason, sourceMessage) = await ResolveCertificateVersionAsync(entity, ns, ct);
        if (version == null)
        {
            entity.Status.Phase = Phases.Failed;
            entity.Status.Targets = resolvedTargets.Count;
            SetCondition(entity, "Ready", "False", sourceReason, sourceMessage);
            metrics.SetTdeDatabases(ns, name, 0, 0, 0);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            // Source may appear (secret created later) — retry.
            return ReconciliationResult<SqlTdeCertificate>.Success(entity, TimeSpan.FromSeconds(60));
        }

        // Age/expiry warnings (never blocks)
        EmitAgeAndExpiryWarnings(entity, version);

        // Check rotation trigger. Interval is measured from the slim-status LastRotationTime;
        // candidate detection is handled live per-database (protector thumbprint comparison), so an
        // unnecessary rotation degrades to a no-op rather than relying on a per-target status tree.
        var shouldRotate = ShouldTriggerRotation(entity);

        // MaxParallelTargets throttling. Every target is visited on every pass: the
        // certificate is deployed and new databases are encrypted everywhere. The limit only caps how
        // many targets get their DEK protector moved in one pass; the others are left pending and
        // picked up by the next passes. Taking the first N targets instead processed the same first
        // target forever and never touched the others, while the status said Ready.
        var maxRotatingTargets = entity.Spec.Rotation?.MaxParallelTargets ?? int.MaxValue;
        var rotatedTargets = 0;
        var targetsToProcess = resolvedTargets;

        // Local roll-ups accumulated across all targets/databases this reconcile. These replace the
        // removed per-target/per-database status arrays — they are derived purely from live SQL truth.
        var outcome = new ReconcileOutcome
        {
            // Carry forward the persisted retirement deadline so a steady-state reconcile (no rotation
            // this pass) re-publishes the same value instead of nulling it and churning the status.
            RetireNotBefore = entity.Status.RetireNotBefore,
            PendingDekRegenerations = new SortedSet<string>(entity.Status.PendingDekRegenerations ?? [], StringComparer.Ordinal),
        };

        foreach (var target in targetsToProcess)
        {
            try
            {
                var rotation = new RotationGate(shouldRotate, rotatedTargets < maxRotatingTargets);

                // Per-target result propagated — no unconditional success counting.
                var targetResult = target.IsAvailabilityGroup
                    ? await ProvisionAgTdeAsync(target, entity, version, rotation, outcome, ct)
                    : await ProvisionInstanceTdeAsync(target, entity, version, rotation, outcome, ct);

                if (targetResult.ProtectorChanged)
                    rotatedTargets++;

                if (targetResult.Ok)
                    outcome.TargetsSucceeded++;
                else
                    outcome.TargetsFailed++;
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

        // Say plainly that superseded certificates are kept, so nobody believes a
        // retirement policy is being enforced.
        if (outcome.RetentionPassive)
            SetCondition(entity, "CertificateRetirement", "False", "NotImplemented",
                "Superseded TDE certificates are retained: the operator never drops a certificate " +
                "(that would make every backup taken while it protected the DEK unrestorable). Drop them manually.");
        entity.Status.EncryptedDatabases = outcome.EncryptedDatabases;
        entity.Status.PendingDatabases = outcome.PendingDatabases;
        entity.Status.FailedDatabases = outcome.FailedDatabases;
        entity.Status.PendingDekRegenerations = outcome.PendingDekRegenerations.Count == 0
            ? null
            : outcome.PendingDekRegenerations.ToList();

        // The first version that becomes active is the baseline of the rotation interval and of the
        // age warnings. Without it, a target with no database to encrypt never stamped a rotation time,
        // so ManualOrInterval saw "never rotated" on every pass and minted a new version each time.
        if (entity.Status.LastRotationTime is null && outcome.ActiveVersion is not null && !outcome.RotationCompletedThisReconcile)
            entity.Status.LastRotationTime = _clock.GetUtcNow().UtcDateTime;

        if (outcome.RotationCompletedThisReconcile)
        {
            entity.Status.LastRotationTime = _clock.GetUtcNow().UtcDateTime;
            await eventPublisher.PublishAsync(entity, "RotationCompleted",
                $"Rotation to version {version.Version} completed on {outcome.TargetsSucceeded} target(s)", ct: ct);

            // Consume the manual-rotation annotation once the rotation completed,
            // mirroring the AG failover orchestrator's annotation lifecycle.
            if (entity.Metadata.Annotations?.Remove("mssql-operator.evariops.com/rotate") == true)
            {
                try
                {
                    var updated = await kubernetesClient.UpdateAsync(entity, ct);
                    entity.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not clear rotate annotation on {Name}; it will be retried next reconcile", name);
                }
            }
        }

        // Overall phase + Ready condition.
        var anyFailure = outcome.TargetsFailed > 0 || outcome.FailedDatabases > 0;
        if (anyFailure && outcome.TargetsSucceeded == 0)
        {
            entity.Status.Phase = Phases.Failed;
            SetCondition(entity, "Ready", "False", "Failed",
                $"TDE deployment failed on all {targetsToProcess.Count} target(s)");
            await eventPublisher.PublishAsync(entity, "RotationFailed",
                $"TDE deployment failed on all {targetsToProcess.Count} target(s)", "Warning", ct);
        }
        else if (anyFailure)
        {
            entity.Status.Phase = Phases.Degraded;
            SetCondition(entity, "Ready", "False", "PartialError",
                $"TDE deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s); {outcome.FailedDatabases} database(s) failed");
        }
        else if (outcome.PendingDatabases > 0 || outcome.IsRotating)
        {
            entity.Status.Phase = Phases.Provisioning;
            SetCondition(entity, "Ready", "True", "CertificateDeployed",
                $"Certificate deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s); rotation in progress");
        }
        else
        {
            entity.Status.Phase = Phases.Ready;
            SetCondition(entity, "Ready", "True", "CertificateDeployed",
                $"Certificate deployed on {outcome.TargetsSucceeded}/{targetsToProcess.Count} target(s)");
        }

        metrics.SetTdeDatabases(ns, name, outcome.EncryptedDatabases, outcome.PendingDatabases, outcome.FailedDatabases);

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        // Success event only when the phase actually transitioned.
        if (previousPhase != entity.Status.Phase)
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
            var certAge = _clock.GetUtcNow().UtcDateTime - lastRotation.Value;
            if (certAge.TotalDays >= rotation.AlertAfterCertificateAgeDays)
            {
                logger.LogWarning(
                    "Certificate {LogicalName} is {AgeDays} days old (alert threshold: {ThresholdDays})",
                    entity.Spec.Certificate.LogicalName, (int)certAge.TotalDays, rotation.AlertAfterCertificateAgeDays);
                SetCondition(entity, "CertificateAge", "True", "CertificateOld",
                    $"Certificate is {(int)certAge.TotalDays} days old, threshold is {rotation.AlertAfterCertificateAgeDays}");
            }
            else
            {
                // Explicitly lower the condition on the healthy path (ConditionOps is
                // transition-aware, so this causes no status churn).
                SetCondition(entity, "CertificateAge", "False", "WithinThreshold",
                    $"Certificate is {(int)certAge.TotalDays} days old");
            }
        }

        // Expiry date warning
        if (version.ExpiryDate != null && DateTime.TryParse(version.ExpiryDate, out var expiry))
        {
            if (expiry < _clock.GetUtcNow().UtcDateTime)
            {
                logger.LogWarning("Certificate {LogicalName} has expired ({ExpiryDate}), continuing reconciliation",
                    entity.Spec.Certificate.LogicalName, version.ExpiryDate);
                SetCondition(entity, "CertificateExpiry", "True", "Expired",
                    $"Certificate expired on {version.ExpiryDate}");
            }
            else if (expiry < _clock.GetUtcNow().UtcDateTime.AddDays(90))
            {
                logger.LogWarning("Certificate {LogicalName} expires soon ({ExpiryDate})",
                    entity.Spec.Certificate.LogicalName, version.ExpiryDate);
                SetCondition(entity, "CertificateExpiry", "True", "ExpiringSoon",
                    $"Certificate expires on {version.ExpiryDate}");
            }
            else
            {
                SetCondition(entity, "CertificateExpiry", "False", "NotExpiring",
                    $"Certificate valid until {version.ExpiryDate}");
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
                // An unknown trigger must not silently behave like "always rotate".
                logger.LogWarning("Unknown rotation trigger '{Trigger}'; rotation disabled until the spec is fixed", rotation.Trigger);
                return false;
        }
    }

    /// <summary>
    /// Whether a NEW certificate version must be minted.
    ///
    /// This is deliberately much stricter than <see cref="ShouldTriggerRotation"/>, which answers
    /// "may the rotation state machine run?" and says yes in the steady state. Minting a version
    /// label creates a new SQL certificate, a new source Secret, and switches the DEK protector of
    /// every selected database. Driving that from <see cref="ShouldTriggerRotation"/> meant a
    /// SqlTdeCertificate with no <c>rotation</c> block (the documented "provision only" case) minted a
    /// fresh timestamped label on EVERY reconcile: ~144 certificates and Secrets a day, the DEK
    /// protector flipping back and forth every few minutes on every production database, and an
    /// endless stream of rotation events.
    ///
    /// A new version is minted only on an explicit, user-initiated trigger.
    /// </summary>
    private bool ShouldMintNewVersion(SqlTdeCertificate entity)
    {
        var rotation = entity.Spec.Rotation;
        if (rotation == null)
            return false; // provision only: never mint a new version by itself

        return rotation.Trigger switch
        {
            "ManualOnly" => HasManualRotationAnnotation(entity),
            "ManualOrInterval" => HasManualRotationAnnotation(entity) || IsIntervalElapsed(entity, rotation.IntervalDays),
            // The operator IS the source here: there is no external "candidate" secret to react to,
            // so this trigger must not mint anything on its own (it does react to a new candidate for
            // KubernetesSecret sources, where the version comes from the secret itself).
            "OnSourceCandidate" => false,
            _ => false,
        };
    }

    private static bool HasManualRotationAnnotation(SqlTdeCertificate entity)
    {
        return entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/rotate", out var val) == true
            && val == "true";
    }

    private bool IsIntervalElapsed(SqlTdeCertificate entity, int intervalDays)
    {
        // Interval is measured from the persisted slim-status LastRotationTime (the one rotation
        // timestamp we still keep) rather than a per-target Rotation.CompletedAt tree.
        var lastRotation = entity.Status?.LastRotationTime;
        if (lastRotation == null) return true; // Never rotated
        return (_clock.GetUtcNow().UtcDateTime - lastRotation.Value).TotalDays >= intervalDays;
    }

    // ── Source Version Resolution ────────────────────────

    /// <summary>Annotation a source Secret must carry to be used by a SqlTdeCertificate of ANOTHER
    /// namespace: a comma-separated list of the consumer namespaces its owner allows.</summary>
    public const string ConsumerNamespacesAnnotation = "mssql-operator.evariops.com/tde-consumer-namespaces";

    private const string NoSourceMessage = "Cannot resolve certificate source";

    private async Task<(CertificateVersionInfo? Version, string Reason, string Message)> ResolveCertificateVersionAsync(
        SqlTdeCertificate entity, string ns, CancellationToken ct)
    {
        var cert = entity.Spec.Certificate;
        var source = cert.Source;

        if (source.Type == "OperatorManaged" && source.OperatorManaged != null)
        {
            var managed = source.OperatorManaged;
            // The version label derives from persisted state (status.ActiveVersion),
            // never from the clock alone. A new label is minted only on first provision or on
            // an explicit rotation trigger — "it's a new day" must not create cert material.
            var activeVersion = entity.Status?.ActiveVersion;
            string versionLabel;
            if (activeVersion == null)
            {
                versionLabel = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd");
            }
            else if (ShouldMintNewVersion(entity))
            {
                versionLabel = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd");
                if (versionLabel == activeVersion)
                    versionLabel = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss"); // same-day re-rotation
            }
            else
            {
                versionLabel = activeVersion;
            }

            var secretName = ResolveTemplate(managed.SourceSecretNameTemplate,
                cert.LogicalName, versionLabel, null, null);
            var existingSecret = await TryGetSecretAsync(ns, secretName, ct);

            if (existingSecret != null)
            {
                return (new CertificateVersionInfo
                {
                    SourceType = "OperatorManaged",
                    SourceNamespace = ns,
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
                }, "", "");
            }

            return (new CertificateVersionInfo
            {
                SourceType = "OperatorManaged",
                SourceNamespace = ns,
                Version = versionLabel,
                SqlCertificateName = ResolveTemplate(managed.SqlCertificateNameTemplate,
                    cert.LogicalName, versionLabel, null, null),
                SourceSecretName = secretName,
                Subject = ResolveTemplate(managed.SubjectTemplate, cert.LogicalName, versionLabel, null, null),
                ExpiryDate = managed.ExpiryDate,
                NeedsGeneration = true,
                EncryptionPasswordSecretRef = managed.SourceSecretEncryptionPasswordRef
            }, "", "");
        }

        if (source.Type == "KubernetesSecret" && source.KubernetesSecret != null)
        {
            var k8sSource = source.KubernetesSecret;
            var discovery = k8sSource.Discovery;
            var secretNs = string.IsNullOrEmpty(discovery.Namespace) ? ns : discovery.Namespace;

            var secrets = await DiscoverSecretsAsync(discovery, secretNs, ct);

            // A Secret of another namespace is only usable with the consent of its owner. The
            // certificate, its private key and its password end up in the consumer's instance (whose
            // sysadmin can export them), so reading any namespace on the CR author's say-so was a
            // cross-tenant exfiltration of TDE keys.
            if (secretNs != ns)
            {
                var consented = secrets.Where(s => AllowsConsumer(s, ns)).ToList();
                if (consented.Count == 0 && secrets.Count > 0)
                    return (null, "CrossNamespaceSourceNotAllowed",
                        $"The certificate Secrets found in namespace '{secretNs}' do not allow namespace '{ns}' to use them: " +
                        $"their owner must list it in the {ConsumerNamespacesAnnotation} annotation.");
                secrets = consented;
            }

            if (secrets.Count == 0) return (null, "NoSource", NoSourceMessage);

            V1Secret? activeSecret = null;
            if (k8sSource.ActivationPolicy == "SecretRole")
            {
                activeSecret = secrets.FirstOrDefault(s =>
                    s.Metadata?.Labels?.TryGetValue("mssql-operator.evariops.com/certificate-role", out var role) == true
                    && role == "candidate")
                    ?? secrets.FirstOrDefault(s =>
                        s.Metadata?.Labels?.TryGetValue("mssql-operator.evariops.com/certificate-role", out var role) == true
                        && role == "active");
            }
            else // NewestReadyCandidate
            {
                activeSecret = secrets
                    .Where(s => s.Metadata?.Labels?.TryGetValue("mssql-operator.evariops.com/ready", out var r) == true && r == "true")
                    .OrderByDescending(s => s.Metadata?.CreationTimestamp)
                    .FirstOrDefault();
            }

            if (activeSecret?.Data == null) return (null, "NoSource", NoSourceMessage);

            var versionRef = FindVersionRef(discovery, activeSecret.Metadata.Name);
            var certKey = versionRef?.CertificateKey ?? "certificate.cer";
            var keyKey = versionRef?.PrivateKeyKey ?? "private-key.pvk";
            var pwdKey = versionRef?.PasswordKey ?? "password";

            var versionValue = activeSecret.Metadata.Labels?.TryGetValue("mssql-operator.evariops.com/certificate-version", out var v) == true ? v : "imported";
            var sqlName = versionRef?.SqlCertificateName
                ?? (activeSecret.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/sql-certificate-name", out var sn) == true ? sn : null)
                ?? $"{cert.LogicalName}_{versionValue}";

            return (new CertificateVersionInfo
            {
                SourceType = "KubernetesSecret",
                SourceNamespace = secretNs,
                Version = versionValue,
                SqlCertificateName = sqlName,
                SourceSecretName = activeSecret.Metadata.Name,
                CertBytes = activeSecret.Data.TryGetValue(certKey, out var cb) ? cb : null,
                KeyBytes = activeSecret.Data.TryGetValue(keyKey, out var kb) ? kb : null,
                Password = activeSecret.Data.TryGetValue(pwdKey, out var pb)
                    ? Encoding.UTF8.GetString(pb) : null,
                NeedsGeneration = false
            }, "", "");
        }

        return (null, "NoSource", NoSourceMessage);
    }

    /// <summary>True when the Secret's owner lists <paramref name="consumerNamespace"/> in
    /// <see cref="ConsumerNamespacesAnnotation"/>. No wildcard: consent is explicit.</summary>
    internal static bool AllowsConsumer(V1Secret secret, string consumerNamespace)
        => secret.Metadata?.Annotations?.TryGetValue(ConsumerNamespacesAnnotation, out var allowed) == true
           && allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Contains(consumerNamespace, StringComparer.Ordinal);

    // ── Instance Provisioning ────────────────────────

    private async Task<TargetResult> ProvisionInstanceTdeAsync(ResolvedTarget target, SqlTdeCertificate entity,
        CertificateVersionInfo version, RotationGate rotation, ReconcileOutcome outcome, CancellationToken ct)
    {
        outcome.NoteRotationPhase("Preparing");

        await using var conn = await connectionFactory.CreateConnectionAsync(target.Host, target.Port, target.SaPassword, "master", ct);

        // Master key (opened with the target's own DMK password)
        await EnsureMasterKeyOpenAsync(conn, target, ct);

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
            return new TargetResult(Ok: true, ProtectorChanged: false);
        }

        // Databases. Encryption of new databases always runs; moving existing DEK
        // protectors is the rotation, subject to the trigger and to the per-pass target budget.
        var dbOutcome = await ProcessDatabasesAsync(conn, entity, version, rotation, $"{target.Kind}/{target.Name}", outcome, ct);
        ConcludeTarget(entity, version, thumbprint, rotation, dbOutcome, outcome);

        // Check old certificate retirement (approval gating only; live evidence already verified)
        if (dbOutcome.AnyProtectorChanged)
            await TryRetireOldCertificateAsync(conn, entity, outcome, ct);
        return new TargetResult(Ok: true, dbOutcome.AnyProtectorChanged);
    }

    /// <summary>Folds the per-database result of one target into the roll-up.</summary>
    private void ConcludeTarget(SqlTdeCertificate entity, CertificateVersionInfo version, string? thumbprint,
        RotationGate rotation, DatabaseOutcome dbOutcome, ReconcileOutcome outcome)
    {
        if (dbOutcome.AnyWaiting)
        {
            outcome.IsRotating = true;
            outcome.NoteRotationPhase("RetentionWaiting");
        }
        else if (dbOutcome.AnyFailed)
        {
            outcome.NoteRotationPhase("Failed");
        }
        else if (dbOutcome.AnyPending)
        {
            // Protector moves deferred by the target budget or DEK regenerations still queued.
            outcome.IsRotating = true;
            outcome.NoteRotationPhase("Rotating");
        }
        else
        {
            outcome.NoteActive(version, thumbprint);
            // Only stamp completion when real rotation work happened this reconcile; otherwise this is a
            // steady-state no-op and stamping LastRotationTime/RetireNotBefore would churn the status.
            if (dbOutcome.AnyProtectorChanged)
            {
                outcome.RotationCompletedThisReconcile = true;
                var retireDays = entity.Spec.Rotation?.Retirement.NotBeforeDays ?? 90;
                outcome.RetireNotBefore = _clock.GetUtcNow().UtcDateTime.AddDays(retireDays);
            }
            outcome.NoteRotationPhase(rotation.Triggered ? "RetentionWaiting" : "Idle");
        }
    }

    // ── AG Provisioning ─────────────────────────────

    /// <summary>Returns true when the target is fully provisioned (every replica carries the
    /// certificate and database processing, if due, ran). Failures are collected per
    /// replica and the DEK protector is never moved while any replica lacks the new certificate
    /// — a failover to that replica would leave databases undecryptable.</summary>
    private async Task<TargetResult> ProvisionAgTdeAsync(ResolvedTarget target, SqlTdeCertificate entity,
        CertificateVersionInfo version, RotationGate rotation, ReconcileOutcome outcome, CancellationToken ct)
    {
        outcome.NoteRotationPhase("Preparing");

        var replicaCount = target.ReplicaCount ?? 3;
        byte[]? certBytes = null;
        byte[]? keyBytes = null;
        string? thumbprint = null;
        string? password = null;
        var replicaFailures = new List<(string Replica, string Error)>();

        // Deploy cert on ALL replicas before any database operation. Per-replica state is no
        // longer persisted; cert presence is verified live via CertificateExistsAsync per replica.
        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{target.Name}-{i}";
            var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);

            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);

                // The DMK must be open in this session to export/import a certificate's private key
                // (it is not auto-opened across sessions on SQL Server for Linux).
                await EnsureMasterKeyOpenAsync(conn, target, ct);

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
            catch (Exception ex) when (i > 0)
            {
                // Secondary failure: keep going (the cert can still reach the other replicas)
                // but remember it — rotation is blocked below until every replica has the cert.
                logger.LogWarning(ex, "Error deploying TDE cert on replica {Replica}", replicaName);
                replicaFailures.Add((replicaName, ex.Message));
            }
            // i == 0 failing means no export source at all: let it propagate — the caller
            // marks the whole target failed immediately.
        }

        outcome.NoteCandidate(version, thumbprint);

        foreach (var (replica, error) in replicaFailures)
            await eventPublisher.PublishAsync(entity, "ReplicaCertDeployFailed",
                $"TDE certificate {version.SqlCertificateName} not deployed on {replica}: {error}", "Warning", ct);

        if (thumbprint == null)
            return new TargetResult(Ok: false, ProtectorChanged: false);

        // Evidence + S3 on primary
        var primary = await targetResolver.FindPrimaryReplicaAsync(target.Name, target.Namespace, target.SaPassword, replicaCount, ct);
        if (primary == null)
            return new TargetResult(Ok: false, ProtectorChanged: false);

        await using var primaryConn = primary.Value.Connection;

        var evidenceOk = await VerifyEvidenceBeforeRotationAsync(entity, version, primaryConn, thumbprint, password, ct);
        if (!evidenceOk)
        {
            outcome.NoteRotationPhase("Preparing");
            return new TargetResult(Ok: false, ProtectorChanged: false);
        }

        // No database may depend on this certificate (new encryption or protector
        // move) while a replica lacks it — a failover to that replica would leave the databases
        // undecryptable. Re-verified on ALL replicas right before: a replica recreated since the
        // deploy loop above may not have the certificate.
        if (replicaFailures.Count > 0)
        {
            outcome.NoteRotationPhase("Preparing");
            return new TargetResult(Ok: false, ProtectorChanged: false);
        }

        var missing = await ReplicasMissingCertificateAsync(target, replicaCount, version.SqlCertificateName, ct);
        if (missing.Count > 0)
        {
            foreach (var replica in missing)
                await eventPublisher.PublishAsync(entity, "ReplicaCertMissing",
                    $"TDE certificate {version.SqlCertificateName} absent on {replica}; database encryption and rotation deferred", "Warning", ct);
            outcome.NoteRotationPhase("Preparing");
            return new TargetResult(Ok: false, ProtectorChanged: false);
        }

        // Process databases on primary only. Encryption of new databases always runs;
        // moving existing DEK protectors is the rotation (trigger + per-pass target budget).
        var dbOutcome = await ProcessDatabasesAsync(primaryConn, entity, version, rotation, $"{target.Kind}/{target.Name}", outcome, ct);
        ConcludeTarget(entity, version, thumbprint, rotation, dbOutcome, outcome);

        if (dbOutcome.AnyProtectorChanged)
            await TryRetireOldCertificateAsync(primaryConn, entity, outcome, ct);
        return new TargetResult(!dbOutcome.AnyFailed, dbOutcome.AnyProtectorChanged);
    }

    /// <summary>Live check of certificate presence on every replica; an unreachable
    /// replica counts as missing — rotation must not proceed on uncertainty.</summary>
    private async Task<List<string>> ReplicasMissingCertificateAsync(ResolvedTarget target, int replicaCount, string certName, CancellationToken ct)
    {
        var missing = new List<string>();
        for (var i = 0; i < replicaCount; i++)
        {
            var replicaName = $"{target.Name}-{i}";
            var host = SqlQuoting.BuildAgPodFqdn(target.Name, i, target.Namespace);
            try
            {
                await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, target.SaPassword, "master", ct);
                if (!await sqlTdeManager.CertificateExistsAsync(conn, certName, ct))
                    missing.Add(replicaName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot verify TDE certificate on replica {Replica}", replicaName);
                missing.Add(replicaName);
            }
        }
        return missing;
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

            // The certificate exists in SQL Server but its source Secret — the ONLY copy
            // of the private key outside the instance — may not. That happens when the Secret creation
            // failed after CREATE CERTIFICATE, or when someone deleted it. The certificate then encrypts
            // databases with a key nothing can restore. Re-export and re-create the Secret.
            // Only for a Secret the operator itself owns (OperatorManaged, in the CR's own
            // namespace). An imported KubernetesSecret source belongs to someone else: re-creating it
            // here copied the private key into whatever namespace the CR lived in.
            if (version.SourceType == "OperatorManaged" && version.SourceSecretName != null
                && await TryGetSecretAsync(entity.Metadata.NamespaceProperty, version.SourceSecretName, ct) is null)
            {
                try
                {
                    var (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, pwd, ct);
                    await CreateSourceSecretAsync(entity, version, certBytes, keyBytes, pwd, ct);
                    await eventPublisher.PublishAsync(entity, "SourceSecretRestored",
                        $"The source Secret '{version.SourceSecretName}' of certificate '{version.SqlCertificateName}' was missing " +
                        "and has been re-exported from SQL Server: the private key had no backup at all.", "Warning", ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "TDE certificate {Cert} exists but its source secret {Secret} is missing and could not be re-exported — " +
                        "the private key has NO backup", version.SqlCertificateName, version.SourceSecretName);
                    await eventPublisher.PublishAsync(entity, "SourceSecretMissing",
                        $"Certificate '{version.SqlCertificateName}' exists in SQL Server but its source Secret " +
                        $"'{version.SourceSecretName}' is missing and could not be re-created ({ex.Message}). The private key " +
                        "currently has no backup: databases encrypted with it would be unrecoverable.", "Warning", ct);
                }
            }

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
        // Evidence 1: Source secret must exist (in its own namespace)
        if (version.SourceSecretName != null)
        {
            var sourceSecret = await TryGetSecretAsync(version.SourceNamespace, version.SourceSecretName, ct);
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

        // The idempotency check must not depend on the NAME, because the default template
        // contains {timestamp}: every reconcile resolved to a brand-new name, found nothing, and created
        // yet another Secret holding the certificate's private key. Identity is the (logical name,
        // thumbprint) pair — look for THAT, whatever the object is called.
        var archived = await FindArchiveSecretAsync(ns, entity.Spec.Certificate.LogicalName, thumbShort, ct);
        if (archived != null)
            return true;

        // Idempotent by name too (a template without {timestamp} resolves to a stable name).
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
                    ["mssql-operator.evariops.com/certificate-logical-name"] = entity.Spec.Certificate.LogicalName,
                    ["mssql-operator.evariops.com/certificate-version"] = version.Version,
                    ["mssql-operator.evariops.com/certificate-thumbprint-short"] = thumbShort,
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

    /// <summary>An existing archive Secret for this certificate version, found by its labels rather
    /// than by a name that may embed a timestamp.</summary>
    private async Task<V1Secret?> FindArchiveSecretAsync(string ns, string logicalName, string thumbShort, CancellationToken ct)
    {
        // An API error must not read as "no archive yet" — that minted a duplicate archive
        // Secret holding the private key. Let it propagate (backoff).
        var selector =
            $"mssql-operator.evariops.com/certificate-logical-name={logicalName}," +
            $"mssql-operator.evariops.com/certificate-thumbprint-short={thumbShort}";
        var secrets = await kubernetesClient.ListAsync<V1Secret>(ns, selector, ct);
        return secrets.FirstOrDefault();
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

        // The bundle uploaded to S3 does NOT carry the password protecting its private
        // key — by design: it references the Kubernetes Secret that holds it. So that Secret
        // must actually hold this password before the object is uploaded. It did not always: when
        // ResolveOrGenerateEncryptionPasswordAsync had to GENERATE one (no dedicated secretRef, no
        // password in the source secret), nothing persisted it — and the S3 backup, the last resort
        // when the cluster is gone, was encrypted with a password that existed only in memory.
        var passwordSecretName = version.EncryptionPasswordSecretRef?.Name ?? version.SourceSecretName;
        var passwordSecretKey = version.EncryptionPasswordSecretRef?.Key ?? "password";
        if (passwordSecretName is null
            || !await SecretHoldsPasswordAsync(entity.Metadata.NamespaceProperty, passwordSecretName, passwordSecretKey, password, ct))
        {
            var message =
                $"S3 backup of certificate '{version.SqlCertificateName}' is refused: the private-key password is not persisted in " +
                $"Secret '{passwordSecretName ?? "(none)"}' (key '{passwordSecretKey}'). Uploading now would produce an encrypted " +
                "bundle nobody can ever decrypt. Reference an existing password Secret with " +
                "certificate.source.operatorManaged.sourceSecretEncryptionPasswordRef, or let the operator create the source Secret first.";
            logger.LogError("{Message}", message);
            await eventPublisher.PublishAsync(entity, "S3BackupRefused", message, "Warning", ct);
            return false;
        }

        try
        {
            var (certBytes, keyBytes) = await sqlTdeManager.BackupCertificateWithPasswordAsync(conn, version.SqlCertificateName, password, ct);

            // The .pvk password never travels in the bundle — it already lives in a
            // persistent Kubernetes Secret; the bundle references that identity instead.
            var bundle = new BackupBundle
            {
                LogicalName = entity.Spec.Certificate.LogicalName,
                Version = version.Version,
                SqlCertificateName = version.SqlCertificateName,
                Thumbprint = thumbprint,
                Certificate = Convert.ToBase64String(certBytes),
                PrivateKey = Convert.ToBase64String(keyBytes),
                PrivateKeyPasswordSecretName = passwordSecretName,
                PrivateKeyPasswordSecretKey = passwordSecretKey,
                CreatedAt = _clock.GetUtcNow().UtcDateTime
            };

            var plaintext = bundle.Serialize();
            try
            {
                // The logical identity is authenticated (AAD) — substitution/replay detected.
                var aad = BuildBundleAad(bundle.LogicalName, bundle.Version, objectKey);
                var encryptedBytes = await EncryptBackup(s3Config.Encryption, plaintext, aad, entity.Metadata.NamespaceProperty, ct);

                await s3Client.PutObjectAsync(s3Conn, objectKey, encryptedBytes, ct);
            }
            finally
            {
                // Zero the cleartext bundle and the raw key material. Residual limit:
                // bundle.PrivateKey/Certificate are base64 strings (immutable on the GC heap) —
                // documented, not fully erasable.
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(certBytes);
            }

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
        SqlTdeCertificate entity, CertificateVersionInfo version, RotationGate rotation, string targetKey,
        ReconcileOutcome outcome, CancellationToken ct)
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
                            result.AnyNewlyEncrypted = true;
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
                    // Moving a protector IS the rotation: only when the trigger fired and the
                    // per-pass target budget allows it; otherwise it is deferred.
                    var currentThumbprint = await sqlTdeManager.GetDekProtectorThumbprintAsync(conn, dbName, ct);
                    var regenKey = $"{targetKey}/{dbName}";

                    var protectorDiffers = !string.Equals(currentThumbprint, newThumbprint, StringComparison.OrdinalIgnoreCase);
                    var protectorChangedForDb = false;
                    if (protectorDiffers && rotation.MayChangeProtector)
                    {
                        await sqlTdeManager.ChangeDekProtectorAsync(conn, dbName, version.SqlCertificateName, ct);
                        protectorChangedForDb = true;
                        result.AnyProtectorChanged = true;
                        await eventPublisher.PublishAsync(entity, "ProtectorRotated",
                            $"Database '{dbName}' DEK protector rotated to {version.SqlCertificateName}", ct: ct);
                    }
                    else if (protectorDiffers && rotation.Triggered)
                    {
                        result.AnyPending = true; // over the target budget: next pass
                        outcome.PendingDatabases++;
                    }

                    outcome.EncryptedDatabases++;

                    // Optional DEK regeneration. Only after the protector moved — a
                    // steady-state reconcile must not regenerate the DEK (full re-encryption scan) over
                    // and over. A regeneration owed from an earlier pass (throttled, or waiting
                    // for a log backup) is remembered in the status and retried until it succeeds; it
                    // used to be lost as soon as the protector no longer changed.
                    var regenerate = entity.Spec.Rotation?.Mode == "ProtectorAndDekRegenerate"
                                     && entity.Spec.Rotation.DekRegeneration != null
                                     && (protectorChangedForDb || outcome.PendingDekRegenerations.Contains(regenKey));
                    if (regenerate)
                    {
                        // Throttle: check maxParallelDatabasesPerTarget
                        if (processedDbs >= maxParallelDbs)
                        {
                            outcome.PendingDekRegenerations.Add(regenKey);
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
                            outcome.PendingDekRegenerations.Remove(regenKey);
                            await eventPublisher.PublishAsync(entity, "DekRegenerated",
                                $"Database '{dbName}' DEK regenerated", ct: ct);
                        }
                        catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (sqlEx.Message.Contains("log backup", StringComparison.OrdinalIgnoreCase))
                        {
                            // WaitingForLogBackup
                            outcome.PendingDekRegenerations.Add(regenKey);
                            result.AnyWaiting = true;
                            outcome.PendingDatabases++;
                            logger.LogWarning("Database '{Database}' requires log backup before DEK regeneration", dbName);
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            outcome.PendingDekRegenerations.Add(regenKey);
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

    /// <summary>
    /// The retirement flow does NOT drop old certificates — and it never did. The
    /// previous implementation looked like it might: it checked notBeforeDays, checked the approval
    /// annotation, then executed a <c>Task.CompletedTask</c> and moved the phase to Idle, which read as
    /// "retirement done". It was worse than that: the whole block was unreachable (the phase is
    /// Rotating at the call sites, never RetentionWaiting, and the phase ratchet cannot lower it), so
    /// <c>retirementPendingApproval</c> never surfaced either and an approval annotation sat there
    /// doing nothing — a live grenade the day a real DROP is wired in.
    ///
    /// Dropping a TDE certificate is genuinely dangerous: any backup taken while it was the DEK
    /// protector — inside the retention window, or in an archive nobody told us about — becomes
    /// unrestorable forever. Until that is implemented with proper DEK/backup verification, the honest
    /// behaviour is to say so: old certificates are KEPT, and the status says exactly that.
    /// </summary>
    private async Task TryRetireOldCertificateAsync(Microsoft.Data.SqlClient.SqlConnection conn,
        SqlTdeCertificate entity, ReconcileOutcome outcome, CancellationToken ct)
    {
        var retirement = entity.Spec.Rotation?.Retirement;
        if (retirement == null) return;

        outcome.RetirementPendingApproval = false;
        outcome.RetentionPassive = true;

        var message =
            "Old TDE certificates are retained, not dropped: the operator does not implement certificate retirement " +
            "(dropping one would make every backup taken while it protected the DEK unrestorable). Drop superseded " +
            "certificates manually once no backup in your retention window still needs them.";

        // Emitted once per rotation, not on every reconcile: RetentionPassive is only set right after a
        // rotation actually happened.
        await eventPublisher.PublishAsync(entity, "CertificateRetentionPassive", message, ct: ct);
        logger.LogInformation("{Message}", message);
    }

    // ── Database selection (live, status-tree-free) ───────────
    // Mirrors the previous DatabaseSelector.BuildSnapshot selection rules but returns only the sorted
    // list of selected names — the per-database phase/hash/observedGeneration snapshot is gone.

    private static List<string> SelectDatabases(DatabaseSelectorSpec selector, List<string> allDatabases)
        => DatabaseSelector.Select(selector, allDatabases);

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>AAD binding a bundle to its logical identity and storage location.</summary>
    internal static byte[] BuildBundleAad(string logicalName, string version, string objectKey)
        => Encoding.UTF8.GetBytes($"{logicalName}\n{version}\n{objectKey}");

    private async Task<byte[]> EncryptBackup(BackupEncryptionSpec encryption,
        byte[] plaintext, byte[] aad, string ns, CancellationToken ct)
    {
        if (encryption.Type == "EnvelopeEncryption" && encryption.EnvelopeEncryption != null)
        {
            var pubKeySecret = await kubernetesClient.GetAsync<V1Secret>(
                encryption.EnvelopeEncryption.PublicKeySecretRef.Name, ns, ct);
            if (pubKeySecret?.Data == null)
                throw new InvalidOperationException("Public key secret not found");
            var pubKey = pubKeySecret.Data[encryption.EnvelopeEncryption.PublicKeySecretRef.Key];
            return backupEncryptor.EncryptWithEnvelope(plaintext, pubKey, aad);
        }

        var keyRef = encryption.AesKey?.KeySecretRef ?? throw new InvalidOperationException("AES key secret ref required");
        var keySecret = await kubernetesClient.GetAsync<V1Secret>(keyRef.Name, ns, ct);
        if (keySecret?.Data == null)
            throw new InvalidOperationException("AES key secret not found");
        // Zero the AES key copy once used (the Secret object holds its own copy;
        // this clears the working buffer we pulled out of it).
        var aesKey = (byte[])keySecret.Data[keyRef.Key].Clone();
        try
        {
            return backupEncryptor.EncryptWithAesKey(plaintext, aesKey, aad);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(aesKey);
        }
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

    /// <summary>
    /// Creates the Database Master Key if needed and opens it for this session.
    ///
    /// The DMK password is NOT the SA password in general: an availability group creates its DMK with
    /// the dedicated <c>masterKeyPasswordSecretRef</c>, so the TDE controller — which used the SA
    /// password everywhere — failed on OPEN MASTER KEY from its very first reconcile, and every TDE
    /// operation on an AG was dead. On a SqlInstance the DMK IS created with the SA password, and a
    /// routine SA rotation then broke every later OPEN. Both cases are handled: the target's own DMK
    /// password first, the SA password as the legacy fallback, with a loud warning.
    /// </summary>
    private async Task EnsureMasterKeyOpenAsync(
        Microsoft.Data.SqlClient.SqlConnection conn, ResolvedTarget target, CancellationToken ct)
    {
        if (!await sqlTdeManager.MasterKeyExistsAsync(conn, ct))
        {
            await sqlTdeManager.CreateMasterKeyAsync(conn, target.EffectiveMasterKeyPassword, ct);
            await sqlTdeManager.OpenMasterKeyAsync(conn, target.EffectiveMasterKeyPassword, ct);
            return;
        }

        try
        {
            await sqlTdeManager.OpenMasterKeyAsync(conn, target.EffectiveMasterKeyPassword, ct);
            return;
        }
        catch (Exception ex) when (target.MasterKeyPassword != null)
        {
            // The DMK predates the dedicated password (or the SA password was rotated): try the SA one.
            logger.LogWarning(ex,
                "OPEN MASTER KEY with the configured master-key password failed on {Kind}/{Target}; retrying with the SA password",
                target.Kind, target.Name);
        }

        try
        {
            await sqlTdeManager.OpenMasterKeyAsync(conn, target.SaPassword, ct);
            logger.LogWarning(
                "The Database Master Key of {Kind}/{Target} is still protected by the SA password, not by masterKeyPasswordSecretRef. " +
                "Rotating the SA password will break TDE on this target: re-protect the DMK " +
                "(ALTER MASTER KEY ADD ENCRYPTION BY PASSWORD / DROP ENCRYPTION BY PASSWORD).",
                target.Kind, target.Name);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot open the Database Master Key of {target.Kind}/{target.Name}: neither the configured master-key password " +
                "nor the SA password works. If the SA password was rotated after the DMK was created, re-protect the DMK with the " +
                "new password (ALTER MASTER KEY ADD ENCRYPTION BY PASSWORD) — TDE operations are blocked until then.", ex);
        }
    }

    /// <summary>True when the referenced Secret really holds this exact password under this key
    /// the S3 bundle is only decryptable if it does.</summary>
    private async Task<bool> SecretHoldsPasswordAsync(string ns, string secretName, string key, string password, CancellationToken ct)
    {
        var secret = await TryGetSecretAsync(ns, secretName, ct);
        if (secret?.Data is null || !secret.Data.TryGetValue(key, out var bytes))
            return false;
        return Encoding.UTF8.GetString(bytes) == password;
    }

    private Task<string?> GetPasswordFromSecretRefAsync(SecretReference secretRef, string ns, CancellationToken ct)
        => string.IsNullOrEmpty(secretRef.Name)
            ? Task.FromResult<string?>(null)
            : SecretValues.TryGetAsync(kubernetesClient, secretRef, ns, ct);

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
                    ["mssql-operator.evariops.com/certificate-logical-name"] = entity.Spec.Certificate.LogicalName,
                    ["mssql-operator.evariops.com/certificate-version"] = version.Version,
                    ["mssql-operator.evariops.com/certificate-role"] = "active",
                    ["mssql-operator.evariops.com/ready"] = "true",
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

    /// <summary>Null only when the Secret does not exist (404). Any other API error
    /// propagates — swallowing it turned an API hiccup into "source missing" (phase Failed, false
    /// SourceSecretMissing events) or into a re-export of the private key.</summary>
    private Task<V1Secret?> TryGetSecretAsync(string ns, string name, CancellationToken ct)
        => kubernetesClient.GetAsync<V1Secret>(name, ns, ct);

    private static CertificateSecretVersionRef? FindVersionRef(CertificateSecretDiscoverySpec discovery, string secretName)
    {
        return discovery.Names?.FirstOrDefault(n =>
            string.Equals(n.SecretName, secretName, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveTemplate(string template, string logicalName, string version,
        string? thumbprintShort, string? clusterName)
    {
        return template
            .Replace("{logicalName}", logicalName)
            .Replace("{version}", version)
            .Replace("{thumbprintShort}", thumbprintShort ?? "")
            .Replace("{timestamp}", _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss"))
            .Replace("{clusterName}", clusterName ?? "");
    }

    private void SetCondition(SqlTdeCertificate entity, string type, string status, string reason, string? message)
        => ConditionOps.Set(entity.Status!.Conditions ??= [], type, status, reason, message);

    private class CertificateVersionInfo
    {
        public string SourceType { get; set; } = string.Empty;
        /// <summary>Namespace of the source Secret (the CR's own, or a consenting one).</summary>
        public string SourceNamespace { get; set; } = string.Empty;
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

        /// <summary>True when a rotation completed and the superseded certificate is simply KEPT
        /// the operator implements no retirement/DROP.</summary>
        public bool RetentionPassive;

        /// <summary>DEK regenerations still owed, keyed <c>{kind}/{target}/{database}</c>:
        /// carried over from the status so a database deferred by the throttle (or waiting for a log
        /// backup) is regenerated on a later pass, when its protector no longer changes.</summary>
        public SortedSet<string> PendingDekRegenerations = new(StringComparer.Ordinal);

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

    /// <summary>Whether this pass may move DEK protectors on a target: the trigger fired
    /// (<see cref="Triggered"/>) and the per-pass target budget is not exhausted.</summary>
    private readonly record struct RotationGate(bool Triggered, bool WithinBudget)
    {
        public bool MayChangeProtector => Triggered && WithinBudget;
    }

    private readonly record struct TargetResult(bool Ok, bool ProtectorChanged);

    private sealed class DatabaseOutcome
    {
        public bool AnyFailed;
        public bool AnyPending;
        public bool AnyWaiting;
        // True only when real rotation work happened this reconcile (a DEK protector was moved).
        // Steady-state reconciles leave it false so LastRotationTime / RetireNotBefore stay stable and
        // the status-write dedup can break the self-reconcile loop.
        public bool AnyProtectorChanged;
        // A database got its first DEK this pass (encryption is not a rotation).
        public bool AnyNewlyEncrypted;
    }
}
