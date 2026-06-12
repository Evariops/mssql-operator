using k8s;
using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlInstance), Verbs = RbacVerb.All)]
public class SqlInstanceController(
    ILogger<SqlInstanceController> logger,
    IKubernetesClient kubernetesClient,
    IStatefulSetBuilder statefulSetBuilder,
    IServiceBuilder serviceBuilder,
    IEventPublisher eventPublisher,
    ICiliumNetworkPolicyBuilder networkPolicyBuilder,
    IMssqlConfBuilder mssqlConfBuilder,
    IMachineKeyManager machineKeyManager,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IServiceMasterKeyManager smkManager) : IEntityController<SqlInstance>
{
    public async Task<ReconciliationResult<SqlInstance>> ReconcileAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlInstance {Name} in {Namespace}", entity.Metadata.Name, entity.Metadata.NamespaceProperty);

        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        // Resolve SA password (still needed for operator SQL connections)
        var saPassword = await ResolveSaPasswordAsync(entity, ns, ct);
        if (saPassword == null)
        {
            logger.LogWarning("SA password secret not found for {Name}", name);
            entity = await UpdateStatus(entity, "Failed", "SaPasswordNotFound", "SA password secret not found", ct);
            return ReconciliationResult<SqlInstance>.Success(entity);
        }

        // Resolve SqlConfiguration if referenced
        SqlConfiguration? sqlConfig = null;
        if (entity.Spec.ConfigurationRef != null)
        {
            sqlConfig = await kubernetesClient.GetAsync<SqlConfiguration>(entity.Spec.ConfigurationRef.Name, ns, ct);
        }

        // Build and apply ConfigMap for mssql.conf
        var confContent = mssqlConfBuilder.Build(sqlConfig?.Spec, entity.Spec.Memory, entity.Spec.Storage);
        var configHash = mssqlConfBuilder.ComputeHash(confContent);
        await ApplyConfigMapAsync(name, ns, confContent, entity, ct);

        // Ensure machine key secret
        var ownerRef = new V1OwnerReference
        {
            ApiVersion = entity.ApiVersion,
            Kind = entity.Kind,
            Name = name,
            Uid = entity.Metadata.Uid,
            Controller = true,
            BlockOwnerDeletion = true
        };
        var machineKeySecretName = await machineKeyManager.EnsureMachineKeySecretAsync(
            name, ns, ownerRef,
            entity.Spec.MachineKeySecretRef?.Name, entity.Spec.MachineKeySecretRef?.Key, ct);

        // Build and apply StatefulSet (uses configHash for rolling restart detection)
        var sts = statefulSetBuilder.Build(entity, configHash, machineKeySecretName);
        await ApplyStatefulSetAsync(sts, ct);

        // Build and apply Service
        var labels = new Dictionary<string, string> { { "mssql-operator.evariops.com/instance", name } };
        var owner = ownerRef;

        var serviceSpec = entity.Spec.Service ?? new ServiceSpec();
        var svc = serviceBuilder.Build(name, ns, labels, serviceSpec, owner);
        await ApplyServiceAsync(svc, ct);

        // Apply memory alignment if spec.memory is defined
        if (entity.Spec.Memory != null)
        {
            entity = await ApplyMemoryAlignmentAsync(entity, saPassword, ns, name, serviceSpec.Port, ct);
        }

        // Backup Service Master Key
        await BackupSmkAsync(entity, saPassword, ns, name, ct);

        // Update status after all side effects have completed
        var endpoint = $"{name}.{ns}.svc.cluster.local:{serviceSpec.Port}";
        entity = await UpdateStatus(entity, "Running", "InstanceRunning", "SQL Server instance is running", ct, endpoint);

        // Apply SqlConfiguration (sp_configure, traceflags, tempdb, databasemail) if referenced
        // The SqlConfiguration's own reconciliation loop handles applying to target instances.
        // Here we just ensure the config was resolved for the ConfigMap generation.
        if (sqlConfig != null)
        {
            logger.LogInformation("SqlConfiguration {Config} resolved for instance {Name}", sqlConfig.Metadata.Name, name);
        }

        // CiliumNetworkPolicy
        if (entity.Spec.NetworkPolicy?.Enabled == true)
        {
            var netpol = networkPolicyBuilder.BuildForInstance(name, ns, labels, serviceSpec.Port, owner,
                entity.Spec.NetworkPolicy.AllowedNamespaces, entity.Spec.NetworkPolicy.OperatorNamespace);
            try
            {
                var existing = await kubernetesClient.GetAsync<GenericCiliumNetworkPolicy>(netpol.Metadata.Name, ns, ct);
                if (existing != null)
                {
                    netpol.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    await kubernetesClient.UpdateAsync(netpol, ct);
                }
                else
                {
                    await kubernetesClient.CreateAsync(netpol, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply CiliumNetworkPolicy for {Name}", name);
            }
        }

        await eventPublisher.PublishAsync(entity, "InstanceProvisioned", $"SQL Server instance {name} is running", ct: ct);
        logger.LogInformation("SqlInstance {Name} reconciled successfully", name);
        // Periodic requeue so instance drift (pod loss, config drift) is re-detected in steady state
        return ReconciliationResult<SqlInstance>.Success(entity, TimeSpan.FromSeconds(60));
    }

    public Task<ReconciliationResult<SqlInstance>> DeletedAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("SqlInstance {Name} deleted", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }

    private async Task ApplyConfigMapAsync(string name, string ns, string confContent, SqlInstance owner, CancellationToken ct)
    {
        var cmName = $"{name}-mssql-conf";
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = cmName,
                NamespaceProperty = ns,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = owner.ApiVersion, Kind = owner.Kind,
                        Name = owner.Metadata.Name, Uid = owner.Metadata.Uid,
                        Controller = true, BlockOwnerDeletion = true
                    }
                ]
            },
            Data = new Dictionary<string, string> { ["mssql.conf"] = confContent }
        };

        var existing = await kubernetesClient.GetAsync<V1ConfigMap>(cmName, ns, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(cm, ct);
        else
        {
            cm.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await kubernetesClient.UpdateAsync(cm, ct);
        }
    }

    private async Task ApplyStatefulSetAsync(V1StatefulSet sts, CancellationToken ct)
    {
        var existing = await kubernetesClient.GetAsync<V1StatefulSet>(sts.Metadata.Name, sts.Metadata.NamespaceProperty, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(sts, ct);
        else
        {
            sts.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await kubernetesClient.UpdateAsync(sts, ct);
        }
    }

    private async Task ApplyServiceAsync(V1Service svc, CancellationToken ct)
    {
        var existing = await kubernetesClient.GetAsync<V1Service>(svc.Metadata.Name, svc.Metadata.NamespaceProperty, ct);
        if (existing == null)
            await kubernetesClient.CreateAsync(svc, ct);
        else
        {
            svc.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            svc.Spec.ClusterIP = existing.Spec.ClusterIP;
            await kubernetesClient.UpdateAsync(svc, ct);
        }
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SaPasswordSecretRef == null) return null;
        try
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SaPasswordSecretRef.Name, ns, ct);
            if (secret?.Data == null) return null;
            var key = entity.Spec.SaPasswordSecretRef.Key;
            return secret.Data.TryGetValue(key, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SqlInstance> UpdateStatus(SqlInstance entity, string phase, string reason, string message, CancellationToken ct, string? endpoint = null)
    {
        entity.Status ??= new SqlInstanceStatus();
        entity.Status.Phase = phase;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Version = entity.Spec.Version;
        entity.Status.Edition = entity.Spec.Edition;
        entity.Status.Endpoint = endpoint;
        // Use ConditionOps so LastTransitionTime only moves on an actual status transition. Rebuilding
        // the condition with DateTime.UtcNow every reconcile would make the status byte-unstable,
        // defeating the status-write dedup and causing a self-reconcile hot loop.
        entity.Status.Conditions ??= [];
        ConditionOps.Set(entity.Status.Conditions, "Ready", phase == "Running" ? "True" : "False", reason, message);
        return await kubernetesClient.UpdateStatusAsync(entity, ct);
    }

    private async Task<SqlInstance> ApplyMemoryAlignmentAsync(SqlInstance entity, string saPassword, string ns, string name, int port, CancellationToken ct)
    {
        var memSpec = entity.Spec.Memory!;
        var totalMb = MemoryCalculator.ParseToMb(memSpec.Total);
        var reserveMb = MemoryCalculator.ParseToMb(memSpec.SystemReserve);
        var sqlMaxMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memSpec);
        var sqlMinMb = MemoryCalculator.CalculateSqlMinMemoryMb(memSpec);

        try
        {
            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);

            var currentConfig = await sqlServerManager.GetCurrentConfigAsync(conn, ct);
            var currentMax = currentConfig.GetValueOrDefault("max server memory (MB)", 0);
            var currentMin = currentConfig.GetValueOrDefault("min server memory (MB)", 0);
            var aligned = currentMax == sqlMaxMb && currentMin == sqlMinMb;

            if (!aligned)
            {
                if (currentMax != sqlMaxMb)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "max server memory (MB)", sqlMaxMb, ct);
                    logger.LogInformation("Applied max server memory = {MaxMb} MB on {Name} (pod limit: {TotalMb} MB, reserve: {ReserveMb} MB)",
                        sqlMaxMb, name, totalMb, reserveMb);
                }

                if (currentMin != sqlMinMb && sqlMinMb > 0)
                {
                    await sqlServerManager.ApplySpConfigureAsync(conn, "min server memory (MB)", sqlMinMb, ct);
                    logger.LogInformation("Applied min server memory = {MinMb} MB on {Name}", sqlMinMb, name);
                }

                await sqlServerManager.ReconfigureAsync(conn, ct: ct);

                await eventPublisher.PublishAsync(entity, "MemoryConfigured",
                    $"max server memory set to {sqlMaxMb} MB (pod limit: {totalMb} MB, system reserve: {reserveMb} MB)", ct: ct);
                aligned = true;
            }

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = aligned
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply memory alignment on {Name} — will retry", name);

            entity.Status!.Memory = new MemoryStatus
            {
                PodLimitMb = totalMb,
                SqlMaxMemoryMb = sqlMaxMb,
                SqlMinMemoryMb = sqlMinMb,
                SystemReserveMb = reserveMb,
                Aligned = false
            };

            await eventPublisher.PublishAsync(entity, "MemoryDriftDetected",
                $"max server memory should be {sqlMaxMb} MB but alignment failed: {ex.Message}", "Warning", ct);
        }

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        return entity;
    }

    private async Task BackupSmkAsync(SqlInstance entity, string saPassword, string ns, string name, CancellationToken ct)
    {
        try
        {
            // Resolve or generate SMK backup password
            var smkPassword = await ResolveSmkPasswordAsync(entity, ns, ct);
            if (smkPassword == null) return;

            var host = $"{name}-0.{name}.{ns}.svc.cluster.local";
            await using var conn = await connectionFactory.CreateConnectionAsync(host, 1433, saPassword, "master", ct);

            // Check if backup already exists
            var smkSecretName = $"{name}-smk-backup";
            var existingSmk = await kubernetesClient.GetAsync<V1Secret>(smkSecretName, ns, ct);
            if (existingSmk?.Data != null && existingSmk.Data.ContainsKey("smk.bak"))
            {
                return; // Already backed up
            }

            // Backup SMK to temp file inside the pod, read it, create secret
            var backupPath = SqlTempPaths.Create("bak");
            await smkManager.BackupSmkAsync(conn, backupPath, smkPassword, ct);

            // Read the backup file from the pod via SQL
            var smkBytes = await ReadFileViaSqlAsync(conn, backupPath, ct);
            if (smkBytes == null || smkBytes.Length == 0)
            {
                logger.LogWarning("SMK backup file is empty for {Name}", name);
                return;
            }

            // Create the SMK backup secret
            var ownerRef = new V1OwnerReference
            {
                ApiVersion = entity.ApiVersion,
                Kind = entity.Kind,
                Name = name,
                Uid = entity.Metadata.Uid,
                Controller = true,
                BlockOwnerDeletion = true
            };

            var smkSecret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = smkSecretName,
                    NamespaceProperty = ns,
                    OwnerReferences = [ownerRef],
                    Annotations = new Dictionary<string, string>
                    {
                        ["argocd.argoproj.io/sync-options"] = "Delete=false"
                    }
                },
                Type = "Opaque",
                Data = new Dictionary<string, byte[]>
                {
                    ["smk.bak"] = smkBytes
                }
            };

            await kubernetesClient.CreateAsync(smkSecret, ct);
            logger.LogInformation("Created SMK backup secret {Secret} for {Name}", smkSecretName, name);

            // The temp .bak stays in the pod's ephemeral /tmp under a unique name: SQL Server on
            // Linux has no native command to delete an arbitrary file (xp_cmdshell is unavailable),
            // so cleanup is left to the pod lifecycle.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to backup SMK for {Name} — will retry on next reconciliation", name);
        }
    }

    private async Task<string?> ResolveSmkPasswordAsync(SqlInstance entity, string ns, CancellationToken ct)
    {
        if (entity.Spec.SmkBackupPasswordSecretRef != null)
        {
            var secret = await kubernetesClient.GetAsync<V1Secret>(entity.Spec.SmkBackupPasswordSecretRef.Name, ns, ct);
            if (secret?.Data != null && secret.Data.TryGetValue(entity.Spec.SmkBackupPasswordSecretRef.Key, out var bytes))
                return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // Generate and store a random password
        var passwordSecretName = $"{entity.Metadata.Name}-smk-backup-password";
        var existing = await kubernetesClient.GetAsync<V1Secret>(passwordSecretName, ns, ct);
        if (existing?.Data != null && existing.Data.TryGetValue("password", out var existingBytes))
            return System.Text.Encoding.UTF8.GetString(existingBytes);

        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var ownerRef = new V1OwnerReference
        {
            ApiVersion = entity.ApiVersion,
            Kind = entity.Kind,
            Name = entity.Metadata.Name,
            Uid = entity.Metadata.Uid,
            Controller = true,
            BlockOwnerDeletion = true
        };

        var newSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = passwordSecretName,
                NamespaceProperty = ns,
                OwnerReferences = [ownerRef],
                Annotations = new Dictionary<string, string>
                {
                    ["argocd.argoproj.io/sync-options"] = "Delete=false"
                }
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password)
            }
        };

        await kubernetesClient.CreateAsync(newSecret, ct);
        logger.LogInformation("Generated SMK backup password secret {Secret}", passwordSecretName);
        return password;
    }

    private static async Task<byte[]?> ReadFileViaSqlAsync(Microsoft.Data.SqlClient.SqlConnection conn, string filePath, CancellationToken ct)
    {
        SqlTempPaths.EnsureSafe(filePath);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlQuoting.QuoteStringLiteral(filePath)}, SINGLE_BLOB) AS x";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[];
    }

}
