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
    Services.Kubernetes.IEventPublisher eventPublisher,
    TimeProvider? timeProvider = null) : IEntityController<SqlConfiguration>
{
    // The clock is injected (TimeProvider.System by default) — time-dependent logic is testable.
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Per-pass state: it lived in controller fields, correct only because the
    /// controller happens to be scoped to one reconcile.</summary>
    private sealed class PassState
    {
        public readonly HashSet<string> RestartRequiredApplied = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The instances where values armed by someone else blocked the RECONFIGURE.</summary>
        public readonly List<string> ReconfigureBlocked = [];
    }

    public async Task<ReconciliationResult<SqlConfiguration>> ReconcileAsync(SqlConfiguration entity, CancellationToken ct)
    {
        logger.LogDebug("Reconciling SqlConfiguration {Name}", entity.Metadata.Name);

        var ns = entity.Metadata.NamespaceProperty;
        var appliedOwners = new List<string>();
        var errors = new List<string>();
        entity.Status ??= new SqlConfigurationStatus();

        // Each kind is listed once per pass; the targets of the other configurations (conflict
        // check) are computed from the same lists instead of one LIST per configuration.
        var allInstances = (await kubernetesClient.ListAsync<SqlInstance>(ns, null, ct)).ToList();
        var allAgs = (await kubernetesClient.ListAsync<SqlAvailabilityGroup>(ns, null, ct)).ToList();
        var targetInstances = SelectInstances(entity, allInstances);
        var targetAgs = SelectAgs(entity, allAgs);

        // Two SqlConfigurations whose selectors overlap and set the same option to
        // different values overwrite each other on every resync — the instance oscillates between two
        // configurations (and flushes its plan cache on each memory flip) with nothing to show for it.
        // Nothing enforced uniqueness; detect the conflict and refuse to be one of the two writers.
        var conflicts = await DetectConflictingConfigurationsAsync(entity, targetInstances, targetAgs, allInstances, allAgs, ns, ct);
        if (conflicts.Count > 0)
        {
            var message =
                $"Refusing to apply: {string.Join("; ", conflicts)}. Two SqlConfigurations setting the same option on the " +
                "same instance would overwrite each other on every reconcile. Narrow the selectors, or merge them into one.";
            await eventPublisher.PublishAsync(entity, "ConfigurationConflict", message, "Warning", ct);
            entity.Status.Phase = Phases.Conflict;
            entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
            ConditionOps.Set(entity.Status.Conditions ??= [], "Applied", "False", "ConfigurationConflict", message);
            entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
            return ReconciliationResult<SqlConfiguration>.Success(entity, TimeSpan.FromMinutes(5));
        }

        var ledger = ConfigurationLedger.Load(entity);
        var pass = new PassState();
        var targets = ConfigTarget.Build(targetInstances, targetAgs, ns);
        var everyTarget = ConfigTarget.Build(allInstances, allAgs, ns);
        ledger.AdoptReplicaKeys(everyTarget);

        // Availability groups reference a SqlConfiguration through
        // spec.instanceTemplate.configurationRef or are selected by labels like instances, and every
        // replica is configured (sp_configure, trace flags and tempdb are per instance).
        foreach (var group in targets.GroupBy(t => t.Owner))
        {
            try
            {
                var saPassword = await SecretValues.TryGetAsync(kubernetesClient, group.First().SaPasswordRef, ns, ct);
                if (saPassword == null)
                {
                    errors.Add($"{group.Key} (SA password not found)");
                    continue;
                }

                foreach (var target in group)
                    await ApplyToInstanceAsync(entity, target, saPassword, ledger, pass, ct);
                appliedOwners.Add(group.Key);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to apply config to {Target}", group.Key);
                // The condition lists the target only; the error text goes to the event.
                errors.Add(group.Key);
                await eventPublisher.PublishAsync(entity, "ConfigurationApplyFailed",
                    $"Could not apply the configuration to {group.Key}: {ex.Message}", "Warning", ct);
            }
        }

        // An instance that left the selection gets back what this configuration changed.
        var selected = targets.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var everyInstance = everyTarget.ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var instance in ledger.Instances.Where(i => !selected.Contains(i)).ToList())
        {
            if (!everyInstance.TryGetValue(instance, out var former))
            {
                ledger.Forget(instance); // gone with its instance
                continue;
            }

            try
            {
                var saPassword = await SecretValues.TryGetAsync(kubernetesClient, former.SaPasswordRef, ns, ct);
                if (saPassword == null)
                    continue;
                await using var connection = await connectionFactory.CreateConnectionAsync(former.Host, SqlEndpoints.ContainerPort, saPassword, "master", ct);
                await ledger.RevertAsync(sqlServerManager, connection, former, ct);
                await eventPublisher.PublishAsync(entity, "ConfigurationReverted",
                    $"{former.Instance} is no longer selected: the trace flags and sp_configure values this configuration changed were restored", ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not revert configuration {Name} on deselected {Instance}; will retry", entity.Metadata.Name, instance);
            }
        }

        // Update status (slim): counts + condition only; the per-instance applied list is
        // emitted as Events, not stored in etcd. Availability groups count as matched targets.
        var matched = targetInstances.Count + targetAgs.Count;
        // Capture prior roll-ups before overwriting so we only re-stamp LastAppliedTime on a real change.
        // sp_configure is applied idempotently every reconcile; stamping the time unconditionally would
        // make the status byte-unstable, defeating the dedup and causing a self-reconcile hot loop.
        var prevGen = entity.Status.ObservedGeneration;
        var prevApplied = entity.Status.AppliedInstances;
        var prevMatched = entity.Status.MatchedInstances;
        entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;
        entity.Status.Phase = errors.Count == 0 ? (matched == 0 ? "Pending" : "Applied") : "PartiallyApplied";
        entity.Status.MatchedInstances = matched;
        entity.Status.AppliedInstances = appliedOwners.Count;
        var appliedChanged = prevGen != entity.Status.ObservedGeneration
            || prevApplied != appliedOwners.Count
            || prevMatched != matched;
        if (appliedOwners.Count > 0 && (appliedChanged || entity.Status.LastAppliedTime is null))
            entity.Status.LastAppliedTime = _clock.GetUtcNow().UtcDateTime;
        ConditionOps.Set(entity.Status.Conditions ??= [], "Applied",
            errors.Count == 0 ? "True" : "False",
            errors.Count == 0 ? "ConfigurationApplied" : "PartiallyApplied",
            errors.Count == 0
                ? $"Applied to {appliedOwners.Count} target(s)"
                : $"Applied to {appliedOwners.Count}, failed on {errors.Count}: {string.Join(", ", errors)} (see events)");

        if (pass.ReconfigureBlocked.Count > 0)
            ConditionOps.Set(entity.Status.Conditions, ReconfigureGuard.Condition, "True", ReconfigureGuard.Condition,
                $"sp_configure values armed by someone else wait for a RECONFIGURE on {string.Join("; ", pass.ReconfigureBlocked)}: " +
                "the operator commits nothing there until they are committed or reverted.");
        else
            ReconfigureGuard.Clear(entity.Status.Conditions);

        // An option that only takes effect after a restart is otherwise invisible — the
        // value sits "armed" in sys.configurations and the behaviour changes at the next unrelated
        // restart, with nothing linking the two.
        ConditionOps.Set(entity.Status.Conditions, "PendingRestart",
            pass.RestartRequiredApplied.Count > 0 ? "True" : "False",
            pass.RestartRequiredApplied.Count > 0 ? "RestartRequiredOptionsApplied" : "NoPendingRestart",
            pass.RestartRequiredApplied.Count > 0
                ? $"Applied option(s) that only take effect after a restart: {string.Join(", ", pass.RestartRequiredApplied.OrderBy(x => x))}"
                : "No configuration change is waiting for a restart");
        if (pass.RestartRequiredApplied.Count > 0)
            await eventPublisher.PublishAsync(entity, "RestartRequired",
                $"sp_configure option(s) {string.Join(", ", pass.RestartRequiredApplied.OrderBy(x => x))} were applied but only take " +
                "effect after a SQL Server restart.", "Warning", ct);

        // Success events only when the applied state actually changed.
        if (appliedChanged)
            foreach (var owner in appliedOwners)
                await eventPublisher.PublishAsync(entity, "ConfigurationApplied", $"Applied to {owner}", ct: ct);
        metrics.SetTargets("SqlConfiguration", ns, entity.Metadata.Name, appliedOwners.Count, matched, errors.Count);

        // The ownership record lives in the status. The annotations earlier versions wrote on
        // the CR itself are removed once imported.
        ledger.Save(entity.Status);
        if (ledger.ImportedLegacyAnnotations && entity.Metadata.Annotations is { } annotations)
        {
            foreach (var key in annotations.Keys.Where(k => k.StartsWith(ConfigurationLedger.LegacyTraceFlagsAnnotationPrefix, StringComparison.Ordinal)).ToList())
                annotations.Remove(key);
            try
            {
                var updated = await kubernetesClient.UpdateAsync(entity, ct);
                entity.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not remove the legacy trace-flag annotations of {Name}", entity.Metadata.Name);
            }
        }

        entity = await kubernetesClient.UpdateStatusAsync(entity, ct);
        logger.LogInformation("SqlConfiguration {Name} reconciled ({Applied} applied, {Errors} errors)",
            entity.Metadata.Name, appliedOwners.Count, errors.Count);
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

    private async Task ApplyToInstanceAsync(SqlConfiguration config, ConfigTarget target, string password,
        ConfigurationLedger ledger, PassState pass, CancellationToken ct)
    {
        var instanceName = target.Instance;
        await using var connection = await connectionFactory.CreateConnectionAsync(target.Host, SqlEndpoints.ContainerPort, password, "master", ct);

        var inUse = await sqlServerManager.GetCurrentConfigAsync(connection, ct);
        var configured = await sqlServerManager.GetConfiguredValuesAsync(connection, ct);
        var spec = config.Spec.SpConfigure;
        var desiredOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What THIS instance has of ours to commit. A controller field carried a failed
        // instance's flag over to the next one, whose RECONFIGURE then committed values a DBA had
        // armed there. Set and committed in one batch, refused while a DBA's value is pending.
        var armed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var restartRequiredArmed = new List<string>();

        async Task Apply(string name, int? desired)
        {
            if (desired == null)
                return;
            desiredOptions.Add(name);

            // An option that needs a restart keeps its old value_in_use until then; compared
            // against value_in_use it was re-issued (with a RECONFIGURE) on every single pass.
            var restartRequired = RestartRequiredOptions.Contains(name);
            var current = (restartRequired ? configured : inUse).TryGetValue(name, out var v) ? v : (int?)null;
            if (current == desired.Value)
                return;

            // Remember what was there before the operator first changed it.
            if (configured.TryGetValue(name, out var before))
                ledger.RecordOriginal(target.Key, name, before);
            armed[name] = desired.Value;
            if (restartRequired)
                restartRequiredArmed.Add(name);
        }

        Task ApplyBool(string name, bool? desired) => Apply(name, desired is { } d ? (d ? 1 : 0) : null);

        async Task ApplyDangerousBool(string name, bool? desired, bool allowDangerous)
        {
            // Enabling a dangerous typed toggle requires spec.allowDangerous; disabling is
            // always permitted. allowDangerous is declared by the author of the CR; the target
            // must opt in too.
            if (desired == true && !allowDangerous)
            {
                await RejectSpConfigureAsync(config, instanceName, name, "dangerous option requires allowDangerous: true", ct);
                return;
            }
            if (desired == true && !target.AllowsPrivileged)
            {
                await RejectSpConfigureAsync(config, instanceName, name, TargetOptInMissing, ct);
                return;
            }
            await ApplyBool(name, desired);
        }

        if (spec != null)
        {
            // When the instance uses spec.memory, skip memory params from SqlConfiguration
            if (target.HasMemorySpec)
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
                await Apply("max server memory (MB)", spec.MaxServerMemoryMb);
                await Apply("min server memory (MB)", spec.MinServerMemoryMb);
            }

            await Apply("max degree of parallelism", spec.MaxDegreeOfParallelism);
            await Apply("cost threshold for parallelism", spec.CostThresholdForParallelism);
            await ApplyBool("optimize for ad hoc workloads", spec.OptimizeForAdHocWorkloads);
            await ApplyBool("backup compression default", spec.BackupCompressionDefault);
            await ApplyBool("backup checksum default", spec.DefaultBackupChecksum);
            await ApplyBool("contained database authentication", spec.ContainedDatabaseAuthentication);

            // These typed toggles map to dangerous sp_configure options. Enabling them
            // requires spec.allowDangerous, just like an advanced[] dangerous entry — otherwise a
            // first-class field would silently bypass the allowlist that governs advanced[].
            await ApplyDangerousBool("remote admin connections", spec.RemoteAdminConnections, spec.AllowDangerous);
            await ApplyDangerousBool("clr enabled", spec.ClrEnabled, spec.AllowDangerous);
            await ApplyDangerousBool("cross db ownership chaining", spec.CrossDbOwnershipChaining, spec.AllowDangerous);

            foreach (var param in spec.Advanced ?? [])
            {
                // The memory guard above only covered the TYPED fields, so the very
                // same options could be pushed through advanced[] and fight the SqlInstance memory
                // alignment — max server memory flip-flopping every few minutes, flushing the plan
                // cache on each flip.
                if (target.IsManagedByTheInstance(param.Name))
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

                if (tier == SpConfigureAllowlist.Tier.Dangerous && param.Value != 0 && !target.AllowsPrivileged)
                {
                    await RejectSpConfigureAsync(config, instanceName, param.Name, TargetOptInMissing, ct);
                    continue;
                }

                await Apply(param.Name, param.Value);
            }
        }

        // An option that left the spec gets back the value the operator found — unless the
        // instance sets it itself now (spec.memory): putting the old value back overwrote its memory
        // alignment, with a RECONFIGURE that flushed the plan cache, until the instance set it again.
        foreach (var (option, original) in ledger.Originals(target.Key))
        {
            if (desiredOptions.Contains(option))
                continue;
            if (!target.IsManagedByTheInstance(option)
                && (!configured.TryGetValue(option, out var current) || current != original))
                armed[option] = original;
        }

        // RECONFIGURE used to run unconditionally on every pass. RECONFIGURE commits
        // EVERY pending sp_configure value, including one a DBA armed manually and had not
        // committed yet — the operator silently pulled the trigger for them, every few minutes.
        // And when there was something of ours to commit, it still did; now it refuses.
        try
        {
            await sqlServerManager.ApplyConfigurationAsync(connection, armed, ct);
        }
        catch (ReconfigureBlockedException ex)
        {
            pass.ReconfigureBlocked.Add($"{instanceName}: {string.Join(", ", ex.Pending)}");
            throw;
        }
        foreach (var (name, value) in armed)
            logger.LogInformation("Applied sp_configure '{Name}' = {Value} on {Instance}", name, value, instanceName);
        pass.RestartRequiredApplied.UnionWith(restartRequiredArmed);
        // The originals of options that left the spec are forgotten once put back.
        foreach (var (option, _) in ledger.Originals(target.Key).ToList())
            if (!desiredOptions.Contains(option))
                ledger.ForgetOriginal(target.Key, option);

        // TraceFlags. The operator only ever disables flags it enabled itself,
        // and never touches unknown ones.
        var desiredFlags = (config.Spec.TraceFlags?.Global ?? []).ToHashSet();
        var startup = (config.Spec.TraceFlags?.Startup ?? []).ToHashSet();
        var ownedByOperator = ledger.TraceFlags(target.Key);
        if (desiredFlags.Count > 0 || ownedByOperator.Count > 0)
        {
            var activeFlags = await sqlServerManager.GetActiveTraceFlagsAsync(connection, ct);

            foreach (var flag in desiredFlags.Except(activeFlags))
            {
                await sqlServerManager.EnableTraceFlagAsync(connection, flag, ct);
                ownedByOperator.Add(flag);
                logger.LogInformation("Enabled traceflag {Flag} on {Instance}", flag, instanceName);
            }

            // Only turn off flags WE turned on, that are no longer wanted, and that are not startup
            // flags of this same configuration.
            foreach (var flag in ownedByOperator.Except(desiredFlags).Except(startup).ToList())
            {
                if (activeFlags.Contains(flag))
                {
                    await sqlServerManager.DisableTraceFlagAsync(connection, flag, ct);
                    logger.LogInformation("Disabled traceflag {Flag} on {Instance}", flag, instanceName);
                }
                ownedByOperator.Remove(flag);
            }

            var foreign = activeFlags.Except(desiredFlags).Except(startup).Except(ownedByOperator).ToList();
            if (foreign.Count > 0)
                await eventPublisher.PublishAsync(config, "UnmanagedTraceFlagDetected",
                    $"Trace flag(s) {string.Join(", ", foreign)} are active on {instanceName} but were not enabled by the operator; " +
                    "leaving them alone. List them in traceFlags.global if the operator should manage them.", ct: ct);
        }

        // TempDB files
        if (config.Spec.TempDb != null)
        {
            var tempDbPath = target.HasTempDbVolume ? "/var/opt/mssql/tempdb" : "/var/opt/mssql/data";

            // Relocate existing tempdb files to dedicated volume if needed
            if (target.HasTempDbVolume)
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

        // The model database settings were accepted and never applied.
        if (config.Spec.ModelDatabase is { } model
            && await sqlServerManager.GetModelDatabaseSettingsAsync(connection, ct) is { } modelNow)
        {
            if (!modelNow.RecoveryModel.Replace("_", "").Equals(model.RecoveryModel.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                await sqlServerManager.SetModelRecoveryModelAsync(connection, model.RecoveryModel, ct);
            if (modelNow.GrowthMb != model.AutoGrowthMb)
                await sqlServerManager.SetModelFileGrowthAsync(connection, modelNow.LogicalName, model.AutoGrowthMb, ct);
        }

        // Database Mail
        if (config.Spec.DatabaseMail?.Enabled == true)
        {
            var configNs = config.Metadata.NamespaceProperty;
            async Task<string?> ResolveSmtpSecret(SecretReference secretRef, CancellationToken token)
                => await SecretValues.TryGetAsync(kubernetesClient, secretRef, configNs, token);
            await databaseMailManager.ConfigureAsync(connection, config.Spec.DatabaseMail, ResolveSmtpSecret, ct);
            logger.LogInformation("Database Mail configured on {Instance}", instanceName);
        }

        // Agent Operators
        if (config.Spec.AgentOperators != null)
        {
            await databaseMailManager.ConfigureOperatorsAsync(connection, config.Spec.AgentOperators, ct);
            logger.LogInformation("Agent operators configured on {Instance}", instanceName);
        }

        logger.LogDebug("Configuration applied to {Instance}", instanceName);
    }

    /// <summary>Availability groups this configuration applies to: by
    /// <c>instanceTemplate.configurationRef</c> or by label selector, exactly like instances.</summary>
    private static List<SqlAvailabilityGroup> SelectAgs(SqlConfiguration config, IEnumerable<SqlAvailabilityGroup> all)
        => all.Where(ag => ag.Spec.InstanceTemplate?.ConfigurationRef?.Name == config.Metadata.Name
                           || SqlConfigurationResolver.SelectorMatches(config, ag.Metadata.Labels))
            .ToList();

    private static List<SqlInstance> SelectInstances(SqlConfiguration config, IEnumerable<SqlInstance> all)
        => all.Where(instance => instance.Spec.ConfigurationRef?.Name == config.Metadata.Name
                                 || SqlConfigurationResolver.SelectorMatches(config, instance.Metadata.Labels))
            .ToList();

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

    /// <summary>
    /// Finds other SqlConfigurations in the namespace whose targets overlap with ours and that set the
    /// same option to a different value. Returns a human-readable description per
    /// conflict; empty when there is none.
    /// </summary>
    private async Task<List<string>> DetectConflictingConfigurationsAsync(
        SqlConfiguration entity, IReadOnlyCollection<SqlInstance> targets, IReadOnlyCollection<SqlAvailabilityGroup> targetAgs,
        IReadOnlyCollection<SqlInstance> allInstances, IReadOnlyCollection<SqlAvailabilityGroup> allAgs, string ns, CancellationToken ct)
    {
        var conflicts = new List<string>();
        if (targets.Count == 0 && targetAgs.Count == 0)
            return conflicts;

        List<SqlConfiguration> others;
        try
        {
            others = (await kubernetesClient.ListAsync<SqlConfiguration>(ns, null, ct))
                .Where(c => c.Metadata.Name != entity.Metadata.Name)
                .Where(c => c.Metadata.DeletionTimestamp is null)
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Best effort: a conflict we cannot see is not worse than the previous behaviour.
            logger.LogDebug(ex, "Could not list SqlConfigurations to check for conflicts");
            return conflicts;
        }

        var mine = FlattenOptions(entity);
        if (mine.Count == 0)
            return conflicts;

        var mineTargets = targets.Select(i => $"SqlInstance/{i.Metadata.Name}")
            .Concat(targetAgs.Select(a => $"SqlAvailabilityGroup/{a.Metadata.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var other in others)
        {
            var shared = SelectInstances(other, allInstances).Select(i => $"SqlInstance/{i.Metadata.Name}")
                .Concat(SelectAgs(other, allAgs).Select(a => $"SqlAvailabilityGroup/{a.Metadata.Name}"))
                .Where(mineTargets.Contains)
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
        Add("backup checksum default", sp.DefaultBackupChecksum is { } bc ? (bc ? 1 : 0) : null);
        Add("contained database authentication", sp.ContainedDatabaseAuthentication is { } c ? (c ? 1 : 0) : null);
        Add("remote admin connections", sp.RemoteAdminConnections is { } r ? (r ? 1 : 0) : null);
        Add("clr enabled", sp.ClrEnabled is { } cl ? (cl ? 1 : 0) : null);
        Add("cross db ownership chaining", sp.CrossDbOwnershipChaining is { } cd ? (cd ? 1 : 0) : null);

        foreach (var param in sp.Advanced ?? [])
            options[param.Name] = param.Value;

        return options;
    }

    private static readonly string TargetOptInMissing =
        $"dangerous option requires the target to carry the annotation {SqlIdentifierRules.AllowPrivilegedRolesAnnotation}=\"true\"";

    /// <summary>Shared rejection path (log + Warning event) for an sp_configure option the reconciler
    /// declines to apply, keeping the advanced[] and typed-toggle gates consistent.</summary>
    private async Task RejectSpConfigureAsync(SqlConfiguration config, string instanceName, string name, string reason, CancellationToken ct)
    {
        logger.LogWarning("Refusing sp_configure '{Name}' on {Instance}: {Reason}", name, instanceName, reason);
        await eventPublisher.PublishAsync(config, "SpConfigureRejected",
            $"sp_configure '{name}' rejected on instance '{instanceName}': {reason}", "Warning", ct);
    }
}
