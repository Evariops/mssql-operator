using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// What a SqlConfiguration changed on each instance: the trace flags it turned on
/// and the sp_configure values it found before changing them. Persisted in the status; the former
/// per-instance annotations on the CR are imported once.
/// </summary>
public sealed class ConfigurationLedger
{
    public const string LegacyTraceFlagsAnnotationPrefix = "mssql-operator.evariops.com/managed-traceflags-";

    private readonly Dictionary<string, SortedSet<int>> _traceFlags = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Instance, string Option), int> _originals = new();

    /// <summary>True when legacy annotations were imported and must be removed from the CR.</summary>
    public bool ImportedLegacyAnnotations { get; private set; }

    public static ConfigurationLedger Load(SqlConfiguration config)
    {
        var ledger = new ConfigurationLedger();
        foreach (var entry in config.Status?.ManagedTraceFlags ?? [])
            ledger._traceFlags[entry.Instance] = new SortedSet<int>(entry.Flags);
        foreach (var entry in config.Status?.OriginalSettings ?? [])
            ledger._originals[(entry.Instance, entry.Option)] = entry.Value;

        foreach (var (key, value) in config.Metadata.Annotations ?? new Dictionary<string, string>())
        {
            if (!key.StartsWith(LegacyTraceFlagsAnnotationPrefix, StringComparison.Ordinal))
                continue;
            ledger.ImportedLegacyAnnotations = true;
            var instance = key[LegacyTraceFlagsAnnotationPrefix.Length..];
            var flags = ledger.TraceFlags(instance);
            foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(raw, out var flag) && flag > 0)
                    flags.Add(flag);
        }

        return ledger;
    }

    public IEnumerable<string> Instances => _traceFlags.Keys.Concat(_originals.Keys.Select(k => k.Instance)).Distinct(StringComparer.Ordinal);

    public bool IsEmpty => _traceFlags.Values.All(f => f.Count == 0) && _originals.Count == 0;

    public SortedSet<int> TraceFlags(string instance)
    {
        if (!_traceFlags.TryGetValue(instance, out var flags))
            _traceFlags[instance] = flags = [];
        return flags;
    }

    /// <summary>Keeps the first value seen: the one in force before the operator touched the option.</summary>
    public void RecordOriginal(string instance, string option, int value) => _originals.TryAdd((instance, option), value);

    public IReadOnlyList<(string Option, int Value)> Originals(string instance)
        => _originals.Where(kv => kv.Key.Instance == instance).Select(kv => (kv.Key.Option, kv.Value)).ToList();

    public void ForgetOriginal(string instance, string option) => _originals.Remove((instance, option));

    public void Forget(string instance)
    {
        _traceFlags.Remove(instance);
        foreach (var key in _originals.Keys.Where(k => k.Instance == instance).ToList())
            _originals.Remove(key);
    }

    /// <summary>
    /// Moves what was recorded under a replica's bare pod name — the legacy annotations, written
    /// before replicas got their own <see cref="ConfigTarget.Key"/> — to the replica's key. A name
    /// that is also a SqlInstance of the namespace stays with the instance.
    /// </summary>
    public void AdoptReplicaKeys(IReadOnlyCollection<ConfigTarget> targets)
    {
        var keys = targets.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var legacy in Instances.Where(i => !keys.Contains(i)).ToList())
        {
            if (targets.FirstOrDefault(t => t.Instance == legacy) is not { } replica)
                continue;
            if (_traceFlags.Remove(legacy, out var flags))
                TraceFlags(replica.Key).UnionWith(flags);
            foreach (var (option, value) in Originals(legacy))
            {
                RecordOriginal(replica.Key, option, value);
                ForgetOriginal(legacy, option);
            }
        }
    }

    /// <summary>Writes the ledger into the status, sorted so it stays byte-stable.</summary>
    public void Save(SqlConfigurationStatus status)
    {
        var flags = _traceFlags.Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ManagedTraceFlagsStatus { Instance = kv.Key, Flags = [.. kv.Value] })
            .ToList();
        var originals = _originals
            .OrderBy(kv => kv.Key.Instance, StringComparer.Ordinal).ThenBy(kv => kv.Key.Option, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new OriginalSettingStatus { Instance = kv.Key.Instance, Option = kv.Key.Option, Value = kv.Value })
            .ToList();
        status.ManagedTraceFlags = flags.Count == 0 ? null : flags;
        status.OriginalSettings = originals.Count == 0 ? null : originals;
    }

    /// <summary>
    /// Undoes what the configuration did on one instance: turns off the trace flags it turned on and
    /// restores the sp_configure values it found, then forgets the instance. An option the instance
    /// now sets itself (<see cref="ConfigTarget.IsManagedByTheInstance"/>) is not put back.
    /// </summary>
    public async Task RevertAsync(ISqlServerManager sqlServerManager, SqlConnection connection, ConfigTarget target, CancellationToken ct)
    {
        var instance = target.Key;
        var active = await sqlServerManager.GetActiveTraceFlagsAsync(connection, ct);
        foreach (var flag in TraceFlags(instance).Intersect(active).ToList())
            await sqlServerManager.DisableTraceFlagAsync(connection, flag, ct);

        var originals = Originals(instance).Where(o => !target.IsManagedByTheInstance(o.Option)).ToList();
        if (originals.Count > 0)
        {
            var configured = await sqlServerManager.GetConfiguredValuesAsync(connection, ct);
            var changed = false;
            foreach (var (option, value) in originals)
            {
                if (configured.TryGetValue(option, out var current) && current == value)
                    continue;
                await sqlServerManager.ApplySpConfigureAsync(connection, option, value, ct);
                changed = true;
            }

            if (changed)
                await sqlServerManager.ReconfigureAsync(connection, ct: ct);
        }

        Forget(instance);
    }
}

/// <summary>One SQL Server process a configuration applies to: a SqlInstance, or one replica of an
/// availability group (<c>Instance</c> is then the pod name). <c>AllowsPrivileged</c>: the owner
/// carries the privileged opt-in annotation — a dangerous option is applied only there,
/// whatever allowDangerous the configuration declares.</summary>
public sealed record ConfigTarget(string Owner, string Instance, string Host, SecretReference? SaPasswordRef,
    bool HasMemorySpec, bool HasTempDbVolume, bool AllowsPrivileged = false)
{
    /// <summary>
    /// The ledger key of this server: the instance name, or <c>{ag}/{pod}</c> for a replica. The pod
    /// name alone can be the name of a SqlInstance of the namespace (an AG <c>sales</c> next to a
    /// SqlInstance <c>sales-0</c>): the two servers shared one ledger entry, and indexing the targets
    /// threw. A "/" never appears in a Kubernetes name.
    /// </summary>
    public string Key => Owner == Instance ? Instance : $"{Owner}/{Instance}";

    /// <summary>The instance sets this option itself (spec.memory): a configuration neither applies
    /// it nor puts back the value it found before.</summary>
    public bool IsManagedByTheInstance(string option) => HasMemorySpec && IsMemoryOption(option);

    public static bool IsMemoryOption(string name)
        => name.Equals("max server memory (MB)", StringComparison.OrdinalIgnoreCase)
           || name.Equals("min server memory (MB)", StringComparison.OrdinalIgnoreCase);

    public static List<ConfigTarget> Build(IEnumerable<SqlInstance> instances, IEnumerable<SqlAvailabilityGroup> ags, string ns)
    {
        var targets = new List<ConfigTarget>();
        foreach (var instance in instances)
        {
            // Pod FQDN => container port 1433, always.
            targets.Add(new ConfigTarget(instance.Metadata.Name, instance.Metadata.Name,
                SqlEndpoints.InstancePodFqdn(instance.Metadata.Name, ns), instance.Spec.SaPasswordSecretRef,
                instance.Spec.Memory != null, instance.Spec.Storage?.TempDb != null,
                SqlIdentifierRules.AllowsPrivilegedRoles(instance.Metadata.Annotations)));
        }

        foreach (var ag in ags)
        {
            var tmpl = ag.Spec.InstanceTemplate ?? new AgInstanceTemplateSpec();
            for (var i = 0; i < ag.Spec.Replicas.Count; i++)
                targets.Add(new ConfigTarget(ag.Metadata.Name, $"{ag.Metadata.Name}-{i}",
                    SqlEndpoints.PodFqdn(ag.Metadata.Name, i, $"{ag.Metadata.Name}-headless", ns), ag.Spec.SaPasswordSecretRef,
                    tmpl.Memory != null, tmpl.Storage?.TempDb != null,
                    SqlIdentifierRules.AllowsPrivilegedRoles(ag.Metadata.Annotations)));
        }

        return targets;
    }
}
