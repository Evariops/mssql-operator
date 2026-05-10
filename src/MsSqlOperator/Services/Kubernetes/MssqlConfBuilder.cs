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
        var sysDbPath = GetVolumePath(storageSpec?.SysDb, "sysDb");
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
