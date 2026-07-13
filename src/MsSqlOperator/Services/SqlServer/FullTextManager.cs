using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>One in-flight full-text population (crawl) on the instance.</summary>
public record FullTextPopulationInfo(string DatabaseName, string PopulationType, string Status, DateTime? StartTime);

/// <summary>
/// What the operator knows about Full-Text on an instance. Before this, the operator
/// was completely blind to FT: pods that took minutes to become usable after a restart
/// looked healthy because the readiness probe answers `SELECT 1` happily while the
/// engine is being crushed by resumed population crawls.
/// </summary>
/// <param name="Installed">Whether the FTS package is present on this instance.</param>
/// <param name="UpgradeOption">sp_fulltext_service 'upgrade_option': Import | Rebuild | Reset | Unknown.</param>
/// <param name="ActivePopulations">Crawls currently queued or running.</param>
public record FullTextStatusInfo(
    bool Installed,
    string UpgradeOption,
    List<FullTextPopulationInfo> ActivePopulations)
{
    public static readonly FullTextStatusInfo NotInstalled = new(false, "Unknown", []);
    public bool HasActivePopulations => ActivePopulations.Count > 0;
}

public interface IFullTextManager
{
    /// <summary>Full-text state of the instance. Never throws: an instance without the FTS package
    /// installed (the default MCR image) reports <see cref="FullTextStatusInfo.NotInstalled"/>.</summary>
    Task<FullTextStatusInfo> GetStatusAsync(SqlConnection connection, CancellationToken ct = default);

    /// <summary>Sets sp_fulltext_service 'upgrade_option': Import (2) keeps the
    /// existing indexes across an engine upgrade; Rebuild (0) / Reset (1) repopulate every catalog
    /// on the first boot after a CU — the full-text storm this operator exists to avoid.</summary>
    Task SetUpgradeOptionAsync(SqlConnection connection, string option, CancellationToken ct = default);
}

public class FullTextManager(ILogger<FullTextManager> logger) : IFullTextManager
{
    public async Task<FullTextStatusInfo> GetStatusAsync(SqlConnection connection, CancellationToken ct = default)
    {
        try
        {
            await using var installedCmd = connection.CreateCommand();
            installedCmd.CommandText = "SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS INT)";
            var installed = Convert.ToInt32(await installedCmd.ExecuteScalarAsync(ct)) == 1;
            if (!installed)
                return FullTextStatusInfo.NotInstalled;

            await using var upgradeCmd = connection.CreateCommand();
            upgradeCmd.CommandText = "SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('UpgradeOption'), -1) AS INT)";
            var upgradeOption = Convert.ToInt32(await upgradeCmd.ExecuteScalarAsync(ct)) switch
            {
                0 => "Rebuild",
                1 => "Reset",
                2 => "Import",
                _ => "Unknown",
            };

            var populations = new List<FullTextPopulationInfo>();
            await using (var popCmd = connection.CreateCommand())
            {
                // sys.dm_fts_index_population only holds rows for crawls that are queued or running,
                // so any row here is an in-flight population — the exact thing a pod restart or a
                // rolling update must not interrupt.
                popCmd.CommandText = """
                    SELECT
                        DB_NAME(p.database_id),
                        ISNULL(p.population_type_description, 'UNKNOWN'),
                        ISNULL(p.status_description, 'UNKNOWN'),
                        p.start_time
                    FROM sys.dm_fts_index_population p
                    """;
                await using var reader = await popCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    populations.Add(new FullTextPopulationInfo(
                        DatabaseName: reader.IsDBNull(0) ? "?" : reader.GetString(0),
                        PopulationType: reader.GetString(1),
                        Status: reader.GetString(2),
                        StartTime: reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
                }
            }

            return new FullTextStatusInfo(true, upgradeOption, populations);
        }
        catch (Exception ex)
        {
            // Full-text is optional: a missing FTS package makes the DMVs unavailable. Never let
            // that fail a reconcile.
            logger.LogDebug(ex, "Could not read full-text status; treating the instance as non-full-text");
            return FullTextStatusInfo.NotInstalled;
        }
    }

    public async Task SetUpgradeOptionAsync(SqlConnection connection, string option, CancellationToken ct = default)
    {
        var value = option.ToLowerInvariant() switch
        {
            "rebuild" => 0,
            "reset" => 1,
            "import" => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "upgradeOption must be Import, Rebuild or Reset"),
        };

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_fulltext_service @action, @value";
        cmd.Parameters.AddWithValue("@action", "upgrade_option");
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
