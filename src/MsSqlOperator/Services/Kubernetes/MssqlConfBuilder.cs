using System.Security.Cryptography;
using System.Text;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Kubernetes;

public interface IMssqlConfBuilder
{
    string Build(SqlConfigurationSpec? configSpec, MemorySpec? memorySpec, StorageSpec? storageSpec);
    string ComputeHash(string content);
}

public class MssqlConfBuilder : IMssqlConfBuilder
{
    private static readonly Dictionary<string, string> VolumePaths = new()
    {
        ["data"] = "/var/opt/mssql/data",
        ["sysDb"] = "/var/opt/mssql/sysdb",
        ["log"] = "/var/opt/mssql/log",
        ["tempDb"] = "/var/opt/mssql/tempdb",
        ["backup"] = "/var/opt/mssql/backup",
        ["errorLogs"] = "/var/opt/mssql/errorlogs",
        ["certBackups"] = "/var/opt/mssql/tde-backups",
    };

    public string Build(SqlConfigurationSpec? configSpec, MemorySpec? memorySpec, StorageSpec? storageSpec)
    {
        var sb = new StringBuilder();

        // EULA
        sb.AppendLine("[EULA]");
        sb.AppendLine("accepteula = Y");
        sb.AppendLine();

        // SQL Agent
        sb.AppendLine("[sqlagent]");
        sb.AppendLine("enabled = true");
        sb.AppendLine();

        // Telemetry
        sb.AppendLine("[telemetry]");
        sb.AppendLine("customerfeedback = false");
        sb.AppendLine();

        // File locations
        sb.AppendLine("[filelocation]");
        var dataPath = configSpec?.ServerSettings?.DefaultDataPath ?? GetVolumePath(storageSpec?.Data, "data");
        var logPath = configSpec?.ServerSettings?.DefaultLogPath ?? GetVolumePath(storageSpec?.Log, "log");
        var backupPath = configSpec?.ServerSettings?.DefaultBackupPath ?? GetVolumePath(storageSpec?.Backup, "backup");
        // The master/system databases MUST live on a persistent volume. A dedicated sysDb
        // volume is mounted at /var/opt/mssql/sysdb only when storageSpec.SysDb is set; when
        // it is not, co-locate master on the always-present, persistent data volume. Pointing
        // it at the unmounted /var/opt/mssql/sysdb (the previous behaviour) left master on the
        // pod's ephemeral root filesystem, so every restart lost the catalog and orphaned the
        // user databases' files on the data PVC.
        var sysDbPath = storageSpec?.SysDb != null ? VolumePaths["sysDb"] : dataPath;
        var errorLogPath = GetVolumePath(storageSpec?.ErrorLogs, "errorLogs");

        sb.AppendLine($"defaultdatadir = {dataPath}");
        sb.AppendLine($"defaultlogdir = {logPath}");
        sb.AppendLine($"defaultbackupdir = {backupPath}");
        sb.AppendLine($"defaultdumpdir = {errorLogPath}");
        sb.AppendLine($"errorlogfile = {errorLogPath}/errorlog");
        sb.AppendLine($"masterdatafile = {sysDbPath}/master.mdf");
        sb.AppendLine($"masterlogfile = {sysDbPath}/mastlog.ldf");
        sb.AppendLine();

        // Memory
        if (memorySpec != null)
        {
            var memoryLimitMb = MemoryCalculator.CalculateSqlMaxMemoryMb(memorySpec);
            sb.AppendLine("[memory]");
            sb.AppendLine($"memorylimitmb = {memoryLimitMb}");
            sb.AppendLine();
        }

        // Trace flags (startup only — global flags are applied at runtime via DBCC TRACEON)
        if (configSpec?.TraceFlags?.Startup is { Count: > 0 } startupFlags)
        {
            sb.AppendLine("[traceflag]");
            for (var i = 0; i < startupFlags.Count; i++)
            {
                sb.AppendLine($"traceflag{i} = {startupFlags[i]}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GetVolumePath(StorageVolumeSpec? vol, string volumeKey)
    {
        return VolumePaths.GetValueOrDefault(volumeKey, $"/var/opt/mssql/{volumeKey}");
    }
}
