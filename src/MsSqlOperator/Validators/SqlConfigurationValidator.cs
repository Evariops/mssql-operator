using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Validators;

[ValidationWebhook(typeof(SqlConfiguration))]
public class SqlConfigurationValidator : ValidationWebhook<SqlConfiguration>
{
    public override ValidationResult Create(SqlConfiguration entity, bool dryRun)
    {
        return Validate(entity);
    }

    public override ValidationResult Update(SqlConfiguration oldEntity, SqlConfiguration entity, bool dryRun)
    {
        return Validate(entity);
    }

    /// <summary>Mount points the operator manages. A server setting may only point inside one of
    /// them: anywhere else is either the ephemeral root filesystem (lost on restart,
    /// read-only by default) or a path that does not exist.</summary>
    private static readonly string[] ManagedMounts =
    [
        "/var/opt/mssql/data",
        "/var/opt/mssql/log",
        "/var/opt/mssql/backup",
        "/var/opt/mssql/sysdb",
        "/var/opt/mssql/tempdb",
        "/var/opt/mssql/errorlogs",
        "/var/opt/mssql/certbackups",
    ];

    private ValidationResult Validate(SqlConfiguration entity)
    {
        var warnings = new List<string>();

        // An empty targetMatchLabels map is not "no targets" but "ALL instances in the
        // namespace" — the widest possible blast radius, silently. Require an explicit selector.
        if (entity.Spec.TargetMatchLabels is { Count: 0 })
            return Fail("targetMatchLabels must not be empty: an empty selector matches EVERY SqlInstance in the namespace. " +
                        "List the labels of the instances this configuration is meant for.", 422);

        // serverSettings paths must live under a managed mount. A path outside them made
        // mssql.conf point master.mdf at a non-existent file: the config-hash changed, the pod restarted,
        // and SQL Server initialised a BLANK master (logins, jobs and the database catalogue gone) or
        // CrashLooped.
        foreach (var (field, path) in new (string, string?)[]
                 {
                     ("defaultDataPath", entity.Spec.ServerSettings?.DefaultDataPath),
                     ("defaultLogPath", entity.Spec.ServerSettings?.DefaultLogPath),
                     ("defaultBackupPath", entity.Spec.ServerSettings?.DefaultBackupPath),
                 })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!ManagedMounts.Any(m => path == m || path.StartsWith(m + "/", StringComparison.Ordinal)))
                return Fail($"serverSettings.{field} ('{path}') must be inside a volume the operator mounts " +
                            $"({string.Join(", ", ManagedMounts)}). A path outside them lands on the pod's ephemeral, " +
                            "read-only root filesystem.", 422);
        }

        // Tempdb sizing is applied verbatim with ALTER DATABASE. A typo (dataFiles: 800,
        // initialSizeMb: 100000) fills the volume and takes the instance down; nothing bounded it.
        if (entity.Spec.TempDb is { } tempDb)
        {
            if (tempDb.DataFiles is < 1 or > 32)
                return Fail("tempDb.dataFiles must be between 1 and 32 (one file per vCPU up to 8 is the usual guidance).", 422);
            if (tempDb.InitialSizeMb is < 8 or > 262144)
                return Fail("tempDb.initialSizeMb must be between 8 and 262144 (256 GiB) per file.", 422);
            if (tempDb.AutoGrowthMb is < 1 or > 65536)
                return Fail("tempDb.autoGrowthMb must be between 1 and 65536.", 422);
            if (tempDb.LogInitialSizeMb is < 8 or > 262144)
                return Fail("tempDb.logInitialSizeMb must be between 8 and 262144.", 422);
            if (tempDb.LogAutoGrowthMb is < 1 or > 65536)
                return Fail("tempDb.logAutoGrowthMb must be between 1 and 65536.", 422);

            var totalGb = (long)tempDb.DataFiles * tempDb.InitialSizeMb / 1024;
            if (totalGb >= 64)
                warnings.Add($"tempDb will pre-allocate about {totalGb} GiB ({tempDb.DataFiles} files × {tempDb.InitialSizeMb} MB); " +
                             "make sure the tempdb volume is large enough.");
        }

        // Global trace flags are applied with DBCC TRACEON(-1) and anything not listed is
        // turned OFF. Only known flags may be requested, and a flag that is also a startup flag of the
        // same CR must not be fought over.
        if (entity.Spec.TraceFlags is { } tf)
        {
            foreach (var flag in (tf.Global ?? []).Concat(tf.Startup ?? []))
            {
                if (!Configuration.TraceFlagAllowlist.IsAllowed(flag))
                    return Fail($"traceFlags: {flag} is not in the allowlist. Allowed: " +
                                $"{string.Join(", ", Configuration.TraceFlagAllowlist.Allowed)}. " +
                                "Open an issue to widen the list.", 422);
            }
        }

        var sp = entity.Spec.SpConfigure;
        if (sp == null) return Success(warnings.ToArray());

        // 0 means UNLIMITED for the full-text bandwidth caps — the opposite of what a
        // user setting a "low" value expects, and the throttle is the mitigation for the very
        // full-text storm this configuration exists to tame.
        foreach (var param in sp.Advanced ?? [])
        {
            if (param.Name.StartsWith("ft ", StringComparison.OrdinalIgnoreCase)
                && param.Name.Contains("bandwidth", StringComparison.OrdinalIgnoreCase)
                && param.Value == 0)
                warnings.Add($"spConfigure.advanced '{param.Name}' = 0 means UNLIMITED, not 'no bandwidth': " +
                             "it disables the full-text crawl throttle rather than tightening it.");
        }

        if (sp.MaxServerMemoryMb is < 128)
        {
            return Fail("MaxServerMemoryMb must be at least 128 MB", 422);
        }

        if (sp.MaxDegreeOfParallelism is < 0 or > 32767)
        {
            return Fail("MaxDegreeOfParallelism must be between 0 and 32767", 422);
        }

        if (sp.CostThresholdForParallelism is < 0)
        {
            return Fail("CostThresholdForParallelism must be non-negative", 422);
        }

        if (sp.MinServerMemoryMb.HasValue && sp.MaxServerMemoryMb.HasValue &&
            sp.MinServerMemoryMb > sp.MaxServerMemoryMb)
        {
            return Fail("MinServerMemoryMb cannot exceed MaxServerMemoryMb", 422);
        }

        // The typed toggles mapping to dangerous sp_configure options require the same
        // explicit opt-in as advanced[] dangerous entries (enabling only; disabling is always fine).
        foreach (var (field, enabled) in new[]
                 {
                     ("clrEnabled", sp.ClrEnabled),
                     ("crossDbOwnershipChaining", sp.CrossDbOwnershipChaining),
                     ("remoteAdminConnections", sp.RemoteAdminConnections),
                 })
        {
            if (enabled == true && !sp.AllowDangerous)
                return Fail($"spConfigure.{field} enables code execution or crosses a trust boundary; set spConfigure.allowDangerous: true to confirm.", 422);
        }

        // Three-tier allowlist for advanced[]. The same classifier is
        // re-applied at reconcile so the policy holds even if this webhook is disabled.
        foreach (var param in sp.Advanced ?? [])
        {
            switch (Configuration.SpConfigureAllowlist.Classify(param.Name))
            {
                case Configuration.SpConfigureAllowlist.Tier.Safe:
                case Configuration.SpConfigureAllowlist.Tier.Dangerous when param.AllowDangerous:
                    continue;
                case Configuration.SpConfigureAllowlist.Tier.Dangerous:
                    return Fail($"spConfigure.advanced: '{param.Name}' enables code execution or crosses a trust boundary; set allowDangerous: true on this entry to confirm.", 422);
                default:
                    return Fail($"spConfigure.advanced: '{param.Name}' is not in the allowlist. Safe options pass directly; dangerous ones need allowDangerous: true; open an issue to widen the list.", 422);
            }
        }

        return Success(warnings.ToArray());
    }
}
