using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlTdeCertificate))]
public class SqlTdeCertificateValidator : ValidationWebhook<SqlTdeCertificate>
{
    public override ValidationResult Create(SqlTdeCertificate entity, bool dryRun) => Validate(entity);
    public override ValidationResult Update(SqlTdeCertificate oldEntity, SqlTdeCertificate entity, bool dryRun) => Validate(entity);

    private ValidationResult Validate(SqlTdeCertificate entity)
    {
        var targetError = TargetSelectorValidator.Validate(entity.Spec.Target);
        if (targetError != null) return Fail(targetError, 422);

        // Certificate
        var cert = entity.Spec.Certificate;
        if (string.IsNullOrWhiteSpace(cert.LogicalName))
            return Fail("certificate.logicalName is required", 422);

        var source = cert.Source;
        if (source.Type != "OperatorManaged" && source.Type != "KubernetesSecret")
            return Fail("certificate.source.type must be 'OperatorManaged' or 'KubernetesSecret'", 422);

        if (source.Type == "OperatorManaged")
        {
            if (source.OperatorManaged == null)
                return Fail("certificate.source.operatorManaged is required when type is OperatorManaged", 422);
            if (string.IsNullOrWhiteSpace(source.OperatorManaged.ExpiryDate))
                return Fail("certificate.source.operatorManaged.expiryDate is required", 422);
        }

        if (source.Type == "KubernetesSecret")
        {
            if (source.KubernetesSecret == null)
                return Fail("certificate.source.kubernetesSecret is required when type is KubernetesSecret", 422);

            var discovery = source.KubernetesSecret.Discovery;
            var hasNames = discovery.Names is { Count: > 0 };
            var hasLabels = discovery.MatchLabels is { Count: > 0 };
            if (!hasNames && !hasLabels)
                return Fail("certificate.source.kubernetesSecret.discovery must specify names or matchLabels", 422);
            // names and matchLabels are mutually exclusive
            if (hasNames && hasLabels)
                return Fail("certificate.source.kubernetesSecret.discovery: names and matchLabels are mutually exclusive", 422);
        }

        // S3 backup validation
        var s3 = cert.BackupToS3;
        if (s3 != null)
        {
            if (string.IsNullOrWhiteSpace(s3.Connection.Endpoint))
                return Fail("certificate.backupToS3.connection.endpoint is required", 422);
            if (string.IsNullOrWhiteSpace(s3.Connection.Bucket))
                return Fail("certificate.backupToS3.connection.bucket is required", 422);
            //
            if (string.IsNullOrWhiteSpace(s3.Connection.ClusterName))
                return Fail("certificate.backupToS3.connection.clusterName is required", 422);
            if (string.IsNullOrWhiteSpace(s3.Connection.CredentialsSecretRef.Name))
                return Fail("certificate.backupToS3.connection.credentialsSecretRef.name is required", 422);

            var enc = s3.Encryption;
            if (enc.Type != "AesKey" && enc.Type != "EnvelopeEncryption")
                return Fail("certificate.backupToS3.encryption.type must be 'AesKey' or 'EnvelopeEncryption'", 422);

            if (enc.Type == "AesKey" && (enc.AesKey == null || string.IsNullOrWhiteSpace(enc.AesKey.KeySecretRef.Name)))
                return Fail("certificate.backupToS3.encryption.aesKey.keySecretRef.name is required", 422);

            if (enc.Type == "EnvelopeEncryption" && (enc.EnvelopeEncryption == null || string.IsNullOrWhiteSpace(enc.EnvelopeEncryption.PublicKeySecretRef.Name)))
                return Fail("certificate.backupToS3.encryption.envelopeEncryption.publicKeySecretRef.name is required", 422);
        }

        // Database scope validation
        var selector = entity.Spec.Databases.Selector;
        if (!selector.AllUserDatabases && selector.Names is not { Count: > 0 } && selector.NamePatterns is not { Count: > 0 })
            return Fail("databases.selector must specify allUserDatabases, names, or namePatterns", 422);

        //
        var validMissingPolicies = new[] { "Ignore", "Warn", "Fail" };
        if (!validMissingPolicies.Contains(selector.MissingDatabasePolicy))
            return Fail($"databases.selector.missingDatabasePolicy must be one of: {string.Join(", ", validMissingPolicies)}", 422);

        var policy = entity.Spec.Databases.Policy;
        var validAlgorithms = new[] { "AES_128", "AES_192", "AES_256", "TRIPLE_DES_3KEY" };
        if (!validAlgorithms.Contains(policy.EncryptionAlgorithm))
            return Fail($"databases.policy.encryptionAlgorithm must be one of: {string.Join(", ", validAlgorithms)}", 422);

        //
        var validNonEncPolicies = new[] { "Encrypt", "Skip", "Warn", "Fail" };
        if (!validNonEncPolicies.Contains(policy.NonEncryptedDatabasePolicy))
            return Fail($"databases.policy.nonEncryptedDatabasePolicy must be one of: {string.Join(", ", validNonEncPolicies)}", 422);

        // Rotation validation
        var rotation = entity.Spec.Rotation;
        if (rotation != null)
        {
            //
            if (rotation.Mode != "ProtectorOnly" && rotation.Mode != "ProtectorAndDekRegenerate")
                return Fail("rotation.mode must be 'ProtectorOnly' or 'ProtectorAndDekRegenerate'", 422);

            //
            var validTriggers = new[] { "ManualOnly", "ManualOrInterval", "OnSourceCandidate" };
            if (!validTriggers.Contains(rotation.Trigger))
                return Fail($"rotation.trigger must be one of: {string.Join(", ", validTriggers)}", 422);

            //
            if (rotation.MaxParallelTargets < 1)
                return Fail("rotation.maxParallelTargets must be >= 1", 422);

            //
            if (rotation.Trigger == "ManualOrInterval" && rotation.IntervalDays < 1)
                return Fail("rotation.intervalDays must be >= 1 when trigger is ManualOrInterval", 422);

            //
            if (rotation.Mode == "ProtectorAndDekRegenerate" && rotation.DekRegeneration == null)
                return Fail("rotation.dekRegeneration is required when mode is ProtectorAndDekRegenerate", 422);

            //
            if (rotation.DekRegeneration != null && rotation.DekRegeneration.MaxParallelDatabasesPerTarget < 1)
                return Fail("rotation.dekRegeneration.maxParallelDatabasesPerTarget must be >= 1", 422);

            //
            var validApprovalModes = new[] { "Manual", "AutomaticWhenSafe" };
            if (!validApprovalModes.Contains(rotation.Retirement.ApprovalMode))
                return Fail($"rotation.retirement.approvalMode must be one of: {string.Join(", ", validApprovalModes)}", 422);
        }

        return Success();
    }
}
