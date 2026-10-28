using System.Globalization;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>What a backup pass did on one server: nothing (the latest version matches the live key),
/// or added <paramref name="Key"/>; <paramref name="Replaced"/> when an earlier version no longer
/// matched the key.</summary>
public sealed record SmkBackupResult(bool Added, string? Key, bool Replaced);

/// <summary>
/// The backups of a Service Master Key, versioned in a retained Secret. A backup used to be
/// overwritten when the key changed — and a key that changed by mistake (a machine key lost, a
/// restore) took with it the only backup that could still decrypt the data on the volumes. Each key
/// now gets its own entry, <c>smk-&lt;modify_date&gt;.bak</c>, next to the earlier ones:
/// <see cref="MaxVersions"/> at most, the oldest pruned first, the latest never. A backup taken by
/// an earlier operator version (<c>smk.bak</c>) counts as the oldest.
/// </summary>
public static partial class SmkBackups
{
    public const int MaxVersions = 10;
    public const string LegacyKey = "smk.bak";

    /// <summary>When the Service Master Key of the latest version was last modified.</summary>
    public const string ModifyDateAnnotation = "mssql-operator.evariops.com/smk-modify-date";

    /// <summary>The data key of the latest version, for the runbooks.</summary>
    public const string LatestKeyAnnotation = "mssql-operator.evariops.com/smk-latest-key";

    [GeneratedRegex(@"^smk-\d{8}T\d{6}Z\.bak$")]
    private static partial Regex VersionKey();

    /// <summary>The data key of the version of a key modified at <paramref name="modifiedAt"/>; a
    /// date read from SQL Server (no kind) is taken as UTC, the engine's clock in the container.</summary>
    public static string KeyFor(DateTime modifiedAt)
    {
        var utc = modifiedAt.Kind == DateTimeKind.Local ? modifiedAt.ToUniversalTime() : modifiedAt;
        return $"smk-{utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.bak";
    }

    /// <summary>The versions held, oldest first.</summary>
    public static List<string> Versions(IDictionary<string, byte[]>? data)
    {
        if (data is null)
            return [];
        var versions = data.Keys.Where(k => VersionKey().IsMatch(k)).Order(StringComparer.Ordinal).ToList();
        if (data.ContainsKey(LegacyKey))
            versions.Insert(0, LegacyKey);
        return versions;
    }

    /// <summary>
    /// True when the SMK changed after the latest version was taken — it can no longer restore this
    /// server. A version without a stamp (an earlier operator version) is stale only
    /// when the key's modify date can be read, so a re-stamp happens once.
    /// </summary>
    public static bool IsStale(V1Secret backup, DateTime? smkModifiedAt)
    {
        if (smkModifiedAt is not { } modified)
            return false; // cannot tell — do not churn the backup

        if (backup.Metadata?.Annotations is not { } annotations
            || !annotations.TryGetValue(ModifyDateAnnotation, out var raw)
            || !DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var backedUpFor))
            return true; // unstamped: refresh once so the guarantee becomes verifiable

        // Second-level tolerance: sys.symmetric_keys stores a datetime, the annotation a round-trip string.
        return modified > backedUpFor.AddSeconds(1);
    }

    /// <summary>The data with <paramref name="key"/> added, and the oldest versions beyond
    /// <see cref="MaxVersions"/> pruned — never <paramref name="key"/> itself.</summary>
    public static Dictionary<string, byte[]> WithVersion(IDictionary<string, byte[]>? data, string key, byte[] bytes)
    {
        var next = data is null ? [] : new Dictionary<string, byte[]>(data);
        next[key] = bytes;
        var versions = Versions(next);
        foreach (var old in versions.Where(k => k != key).Take(Math.Max(0, versions.Count - MaxVersions)).ToList())
            next.Remove(old);
        return next;
    }

    /// <summary>
    /// Makes sure <paramref name="secretName"/> holds a backup of the live Service Master Key of the
    /// server behind <paramref name="connection"/>, adding a version when it does not.
    /// </summary>
    public static async Task<SmkBackupResult> EnsureAsync(SqlConnection connection, string secretName, string ns,
        IKubernetesObject<V1ObjectMeta> owner, string password, IServiceMasterKeyManager smk, IKubernetesClient client,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var existing = await client.GetAsync<V1Secret>(secretName, ns, ct);
        // A backup taken by a legacy operator still carries an ownerReference —
        // migrate it to the retained shape whether or not it needs a new version.
        if (existing is not null)
            existing = await KeyMaterialSecrets.AdoptAsync(client, existing, owner, logger, ct);

        var modifiedAt = await smk.GetSmkModifyDateAsync(connection, ct);
        var held = Versions(existing?.Data).Count > 0;
        if (held && !IsStale(existing!, modifiedAt))
            return new SmkBackupResult(false,
                existing!.Metadata.Annotations?.TryGetValue(LatestKeyAnnotation, out var latest) == true ? latest : null, false);

        var bytes = await smk.BackupSmkAsync(connection, password, ct)
                    ?? throw new InvalidOperationException($"The backup of the service master key for {secretName} came back empty.");
        var key = KeyFor(modifiedAt ?? clock.GetUtcNow().UtcDateTime);

        if (existing is null)
        {
            // No ownerReference — the backup must outlive the CR, since the data it
            // protects (on the PVCs) does.
            var created = KeyMaterialSecrets.Build(secretName, ns, owner, WithVersion(null, key, bytes));
            Stamp(created, key, modifiedAt);
            await client.CreateAsync(created, ct);
            logger.LogInformation("Created SMK backup secret {Secret} ({Key})", secretName, key);
            return new SmkBackupResult(true, key, false);
        }

        existing.Data = WithVersion(existing.Data, key, bytes);
        Stamp(existing, key, modifiedAt);
        await client.UpdateAsync(existing, ct);
        logger.LogWarning("Added version {Key} to SMK backup secret {Secret}", key, secretName);
        return new SmkBackupResult(true, key, held);
    }

    private static void Stamp(V1Secret secret, string key, DateTime? modifiedAt)
    {
        var annotations = secret.Metadata.Annotations ??= new Dictionary<string, string>();
        annotations[LatestKeyAnnotation] = key;
        if (modifiedAt is { } modified)
            annotations[ModifyDateAnnotation] = modified.ToString("O");
    }
}
