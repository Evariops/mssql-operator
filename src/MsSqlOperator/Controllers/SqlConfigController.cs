using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Configuration;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Controllers;

[EntityRbac(typeof(SqlConfiguration), Verbs = RbacVerb.All)]
public class SqlConfigController(
    ILogger<SqlConfigController> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IDatabaseMailManager databaseMailManager,
    Services.Metrics.OperatorMetrics metrics,
    Services.Kubernetes.IEventPublisher eventPublisher) : IEntityController<SqlConfiguration>
{
    public async Task<ReconciliationResult<SqlConfiguration>> ReconcileAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("Reconciling SqlConfiguration {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var appliedInstances = new List<string>();
        var errors = new List<string>();

        // Resolve target instances by labels
        var targetInstances = await ResolveTargetInstancesAsync(entity, ns, ct);

        // Two SqlConfigurations whose selectors overlap and set the same option to
        // different values overwrite each other on every resync — the instance oscillates between two
        // configurations (and flushes its plan cache on each memory flip) with nothing to show for it.
        // Nothing enforced uniqueness; detect the conflict and refuse to be one of the two writers.
        var conflicts = await DetectConflictingConfigurationsAsync(entity, targetInstances, ns, ct);
        if (conflicts.Count > 0)
        {
            var message =
                $"Refusing to apply: {string.Join("; ", conflicts)}. Two SqlConfigurations setting the same option on the " +
                "same instance would overwrite each other on every reconcile. Narrow the selectors, or merge them into one.";
            await eventPublisher.PublishAsync(entity, "ConfigurationConflict", message, "Warning", ct);
            entity.Status ??= new SqlConfigurationStatus();
            entity.Status.Phase = "Conflict";
            entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
            ConditionOps.Set(entity.Status.Conditions ??= [], "Applied", "False", "ConfigurationConflict", message);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlConfiguration>.Success(entity, TimeSpan.FromMinutes(5));
        }

        foreach (var instance in targetInstances)
        {
            try
            {
                var saPassword = await ResolveSaPasswordAsync(instance, ns, ct);
                if (saPassword == null)
                {
                    errors.Add($"{instance.Metadata.Name}: SA password not found");
                    continue;
                }

                // Pod FQDN => container port 1433, always. The previous pairing of
                // the pod FQDN with serviceSpec.Port broke whenever service.port wasn't 1433.
                var host = SqlEndpoints.InstancePodFqdn(instance.Metadata.Name, ns);
                var hasTempDbVolume = instance.Spec.Storage?.TempDb != null;
                await ApplyToInstanceAsync(entity, host, SqlEndpoints.ContainerPort, saPassword,
                    instance.Metadata.Name, instance.Spec.Memory != null, hasTempDbVolume, ct);
                appliedInstances.Add(instance.Metadata.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply config to {Instance}", instance.Metadata.Name);
                errors.Add($"{instance.Metadata.Name}: {ex.Message}");
            }
        }

        // Availability groups reference a SqlConfiguration through
        // spec.instanceTemplate.configurationRef, but the controller only ever walked SqlInstances —
        // so sp_configure, trace flags and tempdb were silently NOT applied to AG replicas. The
        // full-text crawl throttle ("ft crawl bandwidth"), the whole point of the Phase 1 allowlist
        // change, was therefore inexpressible for an AG: the user set it, the CR reported Applied, and
        // nothing happened.
        foreach (var ag in await ResolveTargetAgsAsync(entity, ns, ct))
        {
            var agName = ag.Metadata.Name;
            try
            {
                var saPassword = await ResolveAgSaPasswordAsync(ag, ns, ct);
                if (saPassword == null)
                {
                    errors.Add($"{agName}: SA password not found");
                    continue;
                }

                var tmpl = ag.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
                var hasTempDbVolume = tmpl.Storage?.TempDb != null;
                var hasMemorySpec = tmpl.Memory != null;

                for (var i = 0; i < ag.Spec.Replicas.Count; i++)
                {
                    var replica = $"{agName}-{i}";
                    var host = SqlEndpoints.PodFqdn(agName, i, $"{agName}-headless", ns);
                    await ApplyToInstanceAsync(entity, host, SqlEndpoints.ContainerPort, saPassword,
                        replica, hasMemorySpec, hasTempDbVolume, ct);
                }

                appliedInstances.Add(agName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply config to availability group {Ag}", agName);
                errors.Add($"{agName}: {ex.Message}");
            }
        }

        // Update status (slim): counts + condition only; the per-instance applied list is
        // emitted as Events, not stored in etcd.
        var matched = targetInstances.Count;
        entity.Status ??= new SqlConfigurationStatus();
        // Capture prior roll-ups before overwriting so we only re-stamp LastAppliedTime on a real change.
        // sp_configure is applied idempotently every reconcile; stamping the time unconditionally would
        // make the status byte-unstable, defeating the dedup and causing a self-reconcile hot loop.
        var prevGen = entity.Status.ObservedGeneration;
        var prevApplied = entity.Status.AppliedInstances;
        var prevMatched = entity.Status.MatchedInstances;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Phase = errors.Count == 0 ? (matched == 0 ? "Pending" : "Applied") : "PartiallyApplied";
        entity.Status.MatchedInstances = matched;
        entity.Status.AppliedInstances = appliedInstances.Count;
        var appliedChanged = prevGen != entity.Status.ObservedGeneration
            || prevApplied != appliedInstances.Count
            || prevMatched != matched;
        if (appliedInstances.Count > 0 && (appliedChanged || entity.Status.LastAppliedTime is null))
            entity.Status.LastAppliedTime = DateTime.UtcNow;
        ConditionOps.Set(entity.Status.Conditions ??= [], "Applied",
            errors.Count == 0 ? "True" : "False",
            errors.Count == 0 ? "ConfigurationApplied" : "PartiallyApplied",
            errors.Count == 0
                ? $"Applied to {appliedInstances.Count} instance(s)"
                : $"Applied to {appliedInstances.Count}, failed on {errors.Count}: {string.Join("; ", errors)}");

        // An option that only takes effect after a restart is otherwise invisible — the
        // value sits "armed" in sys.configurations and the behaviour changes at the next unrelated
        // restart, with nothing linking the two.
        ConditionOps.Set(entity.Status.Conditions, "PendingRestart",
            _restartRequiredApplied.Count > 0 ? "True" : "False",
            _restartRequiredApplied.Count > 0 ? "RestartRequiredOptionsApplied" : "NoPendingRestart",
            _restartRequiredApplied.Count > 0
                ? $"Applied option(s) that only take effect after a restart: {string.Join(", ", _restartRequiredApplied.OrderBy(x => x))}"
                : "No configuration change is waiting for a restart");
        if (_restartRequiredApplied.Count > 0)
            await eventPublisher.PublishAsync(entity, "RestartRequired",
                $"sp_configure option(s) {string.Join(", ", _restartRequiredApplied.OrderBy(x => x))} were applied but only take " +
                "effect after a SQL Server restart.", "Warning", ct);

        // Success events only when the applied state actually changed.
        if (appliedChanged)
            foreach (var inst in appliedInstances)
                await eventPublisher.PublishAsync(entity, "ConfigurationApplied", $"Applied to instance {inst}", ct: ct);
        metrics.SetTargets("SqlConfiguration", ns, entity.Metadata.Name, appliedInstances.Count, matched, errors.Count);

        // Persist the trace-flag ownership record before the status write.
        if (_traceFlagOwnershipChanged)
        {
            try
            {
                var updated = await kubernetesClient.UpdateAsync(entity, ct);
                entity.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not persist the managed trace-flag record for {Name}", entity.Metadata.Name);
            }
            _traceFlagOwnershipChanged = false;
        }

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        logger.LogInformation("SqlConfiguration {Name} reconciled ({Applied} applied, {Errors} errors)",
            entity.Metadata.Name, appliedInstances.Count, errors.Count);
        // Partial application retries instead of staying PartiallyApplied forever.
        return errors.Count > 0
            ? ReconciliationResult<SqlConfiguration>.Success(entity, TimeSpan.FromSeconds(30))
            : ReconciliationResult<SqlConfiguration>.Success(entity);
    }

    public Task<ReconciliationResult<SqlConfiguration>> DeletedAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogInformation("SqlConfiguration {Name} deleted", entity.Metadata.Name);
        metrics.ForgetResource("SqlConfiguration", entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlConfiguration>.Success(entity));
    }

    public async Task ApplyToInstanceAsync(SqlConfiguration config, string host, int port, string password, string instanceName, bool instanceHasMemorySpec, bool hasTempDbVolume, CancellationToken ct)
    {
        await using var connection = await connectionFactory.CreateConnectionAsync(host, port, password, "master", ct);

        var currentConfig = await sqlServerManager.GetCurrentConfigAsync(connection, ct);
        var spec = config.Spec.SpConfigure;

        if (spec != null)
        {
            // When the instance uses spec.memory, skip memory params from SqlConfiguration
            if (instanceHasMemorySpec)
            {
                if (spec.MaxServerMemoryMb.HasValue || spec.MinServerMemoryMb.HasValue)
                {
                    logger.LogWarning("SqlConfiguration {Config} defines memory params but instance {Instance} uses spec.memory — ignoring maxServerMemoryMb/minServerMemoryMb",
                        config.Metadata.Name, instanceName);
                    await eventPublisher.PublishAsync(config, "MemoryConfigConflict",
                        $"maxServerMemoryMb/minServerMemoryMb ignored for instance '{instanceName}' (uses spec.memory)", "Warning", ct);
                }
            }
            else
            {
                await ApplyIfDifferent(connection, currentConfig, "max server memory (MB)", spec.MaxServerMemoryMb, ct);
                await ApplyIfDifferent(connection, currentConfig, "min server memory (MB)", spec.MinServerMemoryMb, ct);
            }

            await ApplyIfDifferent(connection, currentConfig, "max degree of parallelism", spec.MaxDegreeOfParallelism, ct);
            await ApplyIfDifferent(connection, currentConfig, "cost threshold for parallelism", spec.CostThresholdForParallelism, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "optimize for ad hoc workloads", spec.OptimizeForAdHocWorkloads, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "backup compression default", spec.BackupCompressionDefault, ct);
            await ApplyBoolIfDifferent(connection, currentConfig, "contained database authentication", spec.ContainedDatabaseAuthentication, ct);

            // These typed toggles map to dangerous sp_configure options. Enabling them
            // requires spec.allowDangerous, just like an advanced[] dangerous entry — otherwise a
            // first-class field would silently bypass the allowlist that governs advanced[].
            await ApplyDangerousBoolIfDifferent(config, instanceName, connection, currentConfig, "remote admin connections", spec.RemoteAdminConnections, spec.AllowDangerous, ct);
            await ApplyDangerousBoolIfDifferent(config, instanceName, connection, currentConfig, "clr enabled", spec.ClrEnabled, spec.AllowDangerous, ct);
            await ApplyDangerousBoolIfDifferent(config, instanceName, connection, currentConfig, "cross db ownership chaining", spec.CrossDbOwnershipChaining, spec.AllowDangerous, ct);

            if (spec.Advanced != null)
            {
                foreach (var param in spec.Advanced)
                {
                    // The memory guard above only covered the TYPED fields, so the very
                    // same options could be pushed through advanced[] and fight the SqlInstance memory
                    // alignment — max server memory flip-flopping every few minutes, flushing the plan
                    // cache on each flip.
                    if (instanceHasMemorySpec && IsMemoryOption(param.Name))
                    {
                        await RejectSpConfigureAsync(config, instanceName, param.Name,
                            "the instance manages its memory through spec.memory; setting it here would make the two writers " +
                            "overwrite each other on every reconcile", ct);
                        continue;
                    }

                    // Defense in depth: the dangerous-option allowlist is also enforced
                    // at admission (SqlConfigurationValidator), but that webhook can be disabled or
                    // bypassed. Re-check here, in the privileged reconciler that actually runs
                    // sp_configure, so xp_cmdshell/clr/etc. can never be enabled without the explicit
                    // allowDangerous opt-in regardless of admission.
                    var tier = SpConfigureAllowlist.Classify(param.Name);
                    if (tier == SpConfigureAllowlist.Tier.Unknown ||
                        (tier == SpConfigureAllowlist.Tier.Dangerous && !param.AllowDangerous))
                    {
                        await RejectSpConfigureAsync(config, instanceName, param.Name,
                            tier == SpConfigureAllowlist.Tier.Unknown
                                ? "not in the sp_configure allowlist"
                                : "dangerous option requires allowDangerous: true", ct);
                        continue;
                    }

                    await ApplyIfDifferent(connection, currentConfig, param.Name, param.Value, ct);
                }
            }

            // RECONFIGURE used to run unconditionally on every pass. RECONFIGURE commits
            // EVERY pending sp_configure value, including one a DBA armed manually and had not
            // committed yet — the operator silently pulled the trigger for them, every few minutes.
            // Only reconfigure when we actually wrote something, and warn about foreign pending values.
            if (_pendingReconfigure)
            {
                await sqlServerManager.ReconfigureAsync(connection, ct: ct);
                _pendingReconfigure = false;
            }
        }

        // TraceFlags. The previous code reconciled the global flag set TOTALLY —
        // every active flag not listed in this CR was turned OFF. That silently disabled the startup
        // flags of the same CR (they are active at runtime but live in mssql.conf, not in traceFlags.global)
        // and any flag a DBA had enabled for an ongoing investigation. The operator now only ever
        // disables flags it enabled itself, and never touches unknown ones.
        if (config.Spec.TraceFlags?.Global != null)
        {
            var desired = config.Spec.TraceFlags.Global.ToHashSet();
            var startup = (config.Spec.TraceFlags.Startup ?? []).ToHashSet();
            var activeFlags = await sqlServerManager.GetActiveTraceFlagsAsync(connection, ct);
            var ownedByOperator = ReadManagedTraceFlags(config, instanceName);

            foreach (var flag in desired.Except(activeFlags))
            {
                await sqlServerManager.EnableTraceFlagAsync(connection, flag, ct);
                ownedByOperator.Add(flag);
                logger.LogInformation("Enabled traceflag {Flag} on {Instance}", flag, instanceName);
            }

            // Only turn off flags WE turned on, that are no longer wanted, and that are not startup
            // flags of this same configuration.
            foreach (var flag in ownedByOperator.Except(desired).Except(startup).Intersect(activeFlags).ToList())
            {
                await sqlServerManager.DisableTraceFlagAsync(connection, flag, ct);
                ownedByOperator.Remove(flag);
                logger.LogInformation("Disabled traceflag {Flag} on {Instance}", flag, instanceName);
            }

            var foreign = activeFlags.Except(desired).Except(startup).Except(ownedByOperator).ToList();
            if (foreign.Count > 0)
                await eventPublisher.PublishAsync(config, "UnmanagedTraceFlagDetected",
                    $"Trace flag(s) {string.Join(", ", foreign)} are active on {instanceName} but were not enabled by the operator; " +
                    "leaving them alone. List them in traceFlags.global if the operator should manage them.", ct: ct);

            WriteManagedTraceFlags(config, instanceName, ownedByOperator);
        }

        // TempDB files
        if (config.Spec.TempDb != null)
        {
            var tempDbPath = hasTempDbVolume ? "/var/opt/mssql/tempdb" : "/var/opt/mssql/data";

            // Relocate existing tempdb files to dedicated volume if needed
            if (hasTempDbVolume)
            {
                var fileLocations = await sqlServerManager.GetTempDbFileLocationsAsync(connection, ct);
                foreach (var (name, fileName) in fileLocations)
                {
                    if (!fileName.StartsWith(tempDbPath))
                    {
                        var newFileName = $"{tempDbPath}/{Path.GetFileName(fileName)}";
                        await sqlServerManager.RelocateTempDbFileAsync(connection, name, newFileName, ct);
                        logger.LogInformation("Relocated TempDB file {Name} from {Old} to {New} on {Instance} (requires restart)",
                            name, fileName, newFileName, instanceName);
                    }
                }
            }

            var currentFiles = await sqlServerManager.GetTempDbFileCountAsync(connection, ct);
            while (currentFiles < config.Spec.TempDb.DataFiles)
            {
                await sqlServerManager.AddTempDbFileAsync(connection, config.Spec.TempDb.InitialSizeMb, config.Spec.TempDb.AutoGrowthMb, tempDbPath, ct);
                currentFiles++;
                logger.LogInformation("Added TempDB file on {Instance}, now at {Count}", instanceName, currentFiles);
            }
        }

        // Database Mail
        if (config.Spec.DatabaseMail?.Enabled == true)
        {
            var configNs = config.Metadata.NamespaceProperty;
            async Task<string?> ResolveSmtpSecret(SecretReference secretRef, CancellationToken token)
            {
                var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, configNs, token);
                if (secret?.Data == null || !secret.Data.TryGetValue(secretRef.Key, out var bytes))
                    return null;
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            await databaseMailManager.ConfigureAsync(connection, config.Spec.DatabaseMail, ResolveSmtpSecret, ct);
            logger.LogInformation("Database Mail configured on {Instance}", instanceName);
        }

        // Agent Operators
        if (config.Spec.AgentOperators != null)
        {
            await databaseMailManager.ConfigureOperatorsAsync(connection, config.Spec.AgentOperators, ct);
            logger.LogInformation("Agent operators configured on {Instance}", instanceName);
        }

        logger.LogInformation("Configuration applied to {Instance}", instanceName);
    }

    /// <summary>Availability groups this configuration applies to: by
    /// <c>instanceTemplate.configurationRef</c> or by label selector, exactly like instances.</summary>
    private async Task<List<SqlAvailabilityGroup>> ResolveTargetAgsAsync(SqlConfiguration config, string ns, CancellationToken ct)
    {
        var targets = new List<SqlAvailabilityGroup>();
        List<SqlAvailabilityGroup> all;
        try
        {
            all = [.. await kubernetesClient.ListAsync<SqlAvailabilityGroup>(ns, null, ct)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list SqlAvailabilityGroups for SqlConfiguration {Name}", config.Metadata.Name);
            return targets;
        }

        foreach (var ag in all)
        {
            if (ag.Spec.InstanceTemplate?.ConfigurationRef?.Name == config.Metadata.Name)
            {
                targets.Add(ag);
                continue;
            }

            if (config.Spec.TargetMatchLabels is { Count: > 0 } && ag.Metadata.Labels != null)
            {
                var allMatch = config.Spec.TargetMatchLabels.All(kv =>
                    ag.Metadata.Labels.TryGetValue(kv.Key, out var val) && val == kv.Value);
                if (allMatch)
                    targets.Add(ag);
            }
        }

        return targets;
    }

    private async Task<string?> ResolveAgSaPasswordAsync(SqlAvailabilityGroup ag, string ns, CancellationToken ct)
    {
        var secretRef = ag.Spec.SaPasswordSecretRef;
        if (secretRef == null) return null;
        var secret = await kubernetesClient.GetAsync<V1Secret>(secretRef.Name, ns, ct);
        if (secret?.Data == null) return null;
        return secret.Data.TryGetValue(secretRef.Key, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;
    }

    private async Task<List<SqlInstance>> ResolveTargetInstancesAsync(SqlConfiguration config, string ns, CancellationToken ct)
    {
        var allInstances = await kubernetesClient.ListAsync<SqlInstance>(ns, null, ct);
        var targets = new List<SqlInstance>();

        foreach (var instance in allInstances)
        {
            // Match by configurationRef
            if (instance.Spec.ConfigurationRef?.Name == config.Metadata.Name)
            {
                targets.Add(instance);
                continue;
            }

            // Match by targetMatchLabels.
            // An EMPTY map used to match everything — `All()` on an empty sequence is
            // true — so `targetMatchLabels: {}` silently applied the configuration (sp_configure,
            // trace flags, tempdb) to every SqlInstance in the namespace. An empty selector now
            // matches nothing; the webhook rejects it outright, and this is the defence in depth.
            if (config.Spec.TargetMatchLabels is { Count: > 0 } && instance.Metadata.Labels != null)
            {
                var allMatch = config.Spec.TargetMatchLabels.All(kv =>
                    instance.Metadata.Labels.TryGetValue(kv.Key, out var val) && val == kv.Value);
                if (allMatch)
                {
                    targets.Add(instance);
                }
            }
        }

        return targets;
    }

    private async Task<string?> ResolveSaPasswordAsync(SqlInstance instance, string ns, CancellationToken ct)
    {
        // Null only on 404; transport errors propagate (runtime backoff).
        return await SecretValues.TryGetAsync(kubernetesClient, instance.Spec.SaPasswordSecretRef, ns, ct);
    }

    /// <summary>Set when this pass actually wrote an sp_configure value, so RECONFIGURE only runs when
    /// there is something of ours to commit.</summary>
    private bool _pendingReconfigure;

    /// <summary>sp_configure options that only take effect after a restart. Comparing
    /// them against <c>value_in_use</c> (what GetCurrentConfigAsync returns) makes the operator
    /// re-issue them on every single pass — and the value stays silently "armed" until someone
    /// restarts the instance, at which point the behaviour changes with no deployment.</summary>
    private static readonly HashSet<string> RestartRequiredOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "max worker threads",
        "c2 audit mode",
        "common criteria compliance enabled",
        "lightweight pooling",
        "priority boost",
        "affinity mask",
        "affinity64 mask",
        "affinity I/O mask",
        "affinity64 I/O mask",
        "remote access",
        "external scripts enabled",
        "clr enabled",
    };

    private static bool IsMemoryOption(string name)
        => name.Equals("max server memory (MB)", StringComparison.OrdinalIgnoreCase)
           || name.Equals("min server memory (MB)", StringComparison.OrdinalIgnoreCase);

    private async Task ApplyIfDifferent(Microsoft.Data.SqlClient.SqlConnection connection, Dictionary<string, int> current, string name, int? desired, CancellationToken ct)
    {
        if (desired == null) return;
        if (current.TryGetValue(name, out var currentValue) && currentValue == desired.Value) return;

        await sqlServerManager.ApplySpConfigureAsync(connection, name, desired.Value, ct);
        _pendingReconfigure = true;
        logger.LogInformation("Applied sp_configure '{Name}' = {Value}", name, desired.Value);

        if (RestartRequiredOptions.Contains(name))
            _restartRequiredApplied.Add(name);
    }

    private readonly HashSet<string> _restartRequiredApplied = new(StringComparer.OrdinalIgnoreCase);

    // ── Trace-flag ownership ────────────
    // The operator must only ever disable trace flags it enabled itself. Ownership is recorded per
    // instance in an annotation on the SqlConfiguration (the CR is the only durable place available;
    // the status is reserved for user-facing state).
    private const string ManagedTraceFlagsAnnotationPrefix = "mssql-operator.evariops.com/managed-traceflags-";

    private static HashSet<int> ReadManagedTraceFlags(SqlConfiguration config, string instanceName)
    {
        var key = ManagedTraceFlagsAnnotationPrefix + instanceName;
        if (config.Metadata.Annotations is { } annotations
            && annotations.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var f) ? f : -1)
                .Where(f => f > 0)
                .ToHashSet();
        }
        return [];
    }

    private void WriteManagedTraceFlags(SqlConfiguration config, string instanceName, HashSet<int> flags)
    {
        var key = ManagedTraceFlagsAnnotationPrefix + instanceName;
        config.Metadata.Annotations ??= new Dictionary<string, string>();
        var value = string.Join(",", flags.OrderBy(f => f));
        if (config.Metadata.Annotations.TryGetValue(key, out var existing) && existing == value)
            return;
        if (value.Length == 0)
            config.Metadata.Annotations.Remove(key);
        else
            config.Metadata.Annotations[key] = value;
        _traceFlagOwnershipChanged = true;
    }

    private bool _traceFlagOwnershipChanged;

    /// <summary>
    /// Finds other SqlConfigurations in the namespace whose targets overlap with ours and that set the
    /// same option to a different value. Returns a human-readable description per
    /// conflict; empty when there is none.
    /// </summary>
    private async Task<List<string>> DetectConflictingConfigurationsAsync(
        SqlConfiguration entity, IReadOnlyCollection<SqlInstance> targets, string ns, CancellationToken ct)
    {
        var conflicts = new List<string>();
        if (targets.Count == 0)
            return conflicts;

        List<SqlConfiguration> others;
        try
        {
            others = (await kubernetesClient.ListAsync<SqlConfiguration>(ns, null, ct))
                .Where(c => c.Metadata.Name != entity.Metadata.Name)
                .Where(c => c.Metadata.DeletionTimestamp is null)
                .ToList();
        }
        catch (Exception ex)
        {
            // Best effort: a conflict we cannot see is not worse than the previous behaviour.
            logger.LogDebug(ex, "Could not list SqlConfigurations to check for conflicts");
            return conflicts;
        }

        var mine = FlattenOptions(entity);
        if (mine.Count == 0)
            return conflicts;

        foreach (var other in others)
        {
            var otherTargets = await ResolveTargetInstancesAsync(other, ns, ct);
            var shared = otherTargets.Select(i => i.Metadata.Name)
                .Intersect(targets.Select(i => i.Metadata.Name))
                .ToList();
            if (shared.Count == 0)
                continue;

            var theirs = FlattenOptions(other);
            foreach (var (option, value) in mine)
            {
                if (theirs.TryGetValue(option, out var otherValue) && otherValue != value)
                    conflicts.Add(
                        $"SqlConfiguration '{other.Metadata.Name}' sets '{option}' to {otherValue} while this one sets it to {value}, " +
                        $"and both target {string.Join(", ", shared)}");
            }
        }

        return conflicts;
    }

    /// <summary>Every sp_configure option a configuration sets, typed fields and advanced[] alike.</summary>
    private static Dictionary<string, int> FlattenOptions(SqlConfiguration config)
    {
        var options = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sp = config.Spec.SpConfigure;
        if (sp is null)
            return options;

        void Add(string name, int? value)
        {
            if (value.HasValue) options[name] = value.Value;
        }

        Add("max server memory (MB)", sp.MaxServerMemoryMb);
        Add("min server memory (MB)", sp.MinServerMemoryMb);
        Add("max degree of parallelism", sp.MaxDegreeOfParallelism);
        Add("cost threshold for parallelism", sp.CostThresholdForParallelism);
        Add("optimize for ad hoc workloads", sp.OptimizeForAdHocWorkloads is { } o ? (o ? 1 : 0) : null);
        Add("backup compression default", sp.BackupCompressionDefault is { } b ? (b ? 1 : 0) : null);
        Add("contained database authentication", sp.ContainedDatabaseAuthentication is { } c ? (c ? 1 : 0) : null);
        Add("remote admin connections", sp.RemoteAdminConnections is { } r ? (r ? 1 : 0) : null);
        Add("clr enabled", sp.ClrEnabled is { } cl ? (cl ? 1 : 0) : null);
        Add("cross db ownership chaining", sp.CrossDbOwnershipChaining is { } cd ? (cd ? 1 : 0) : null);

        foreach (var param in sp.Advanced ?? [])
            options[param.Name] = param.Value;

        return options;
    }

    /// <summary>Refuse enabling a dangerous typed toggle (clr/crossdb/remote-admin)
    /// unless spec.allowDangerous is set. Disabling (false) is always permitted.</summary>
    private async Task ApplyDangerousBoolIfDifferent(SqlConfiguration config, string instanceName,
        Microsoft.Data.SqlClient.SqlConnection connection, Dictionary<string, int> current,
        string name, bool? desired, bool allowDangerous, CancellationToken ct)
    {
        if (desired == true && !allowDangerous)
        {
            await RejectSpConfigureAsync(config, instanceName, name, "dangerous option requires allowDangerous: true", ct);
            return;
        }
        await ApplyBoolIfDifferent(connection, current, name, desired, ct);
    }

    /// <summary>Shared rejection path (log + Warning event) for an sp_configure option the reconciler
    /// declines to apply, keeping the advanced[] and typed-toggle gates consistent.</summary>
    private async Task RejectSpConfigureAsync(SqlConfiguration config, string instanceName, string name, string reason, CancellationToken ct)
    {
        logger.LogWarning("Refusing sp_configure '{Name}' on {Instance}: {Reason}", name, instanceName, reason);
        await eventPublisher.PublishAsync(config, "SpConfigureRejected",
            $"sp_configure '{name}' rejected on instance '{instanceName}': {reason}", "Warning", ct);
    }

    private async Task ApplyBoolIfDifferent(Microsoft.Data.SqlClient.SqlConnection connection, Dictionary<string, int> current, string name, bool? desired, CancellationToken ct)
    {
        if (desired == null) return;
        await ApplyIfDifferent(connection, current, name, desired.Value ? 1 : 0, ct);
    }
}
