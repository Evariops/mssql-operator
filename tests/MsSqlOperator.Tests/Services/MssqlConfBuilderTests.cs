using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Tests.Services;

public class MssqlConfBuilderTests
{
    private readonly MssqlConfBuilder _builder = new();

    [Fact]
    public void Build_WithDefaults_ContainsEulaAndAgent()
    {
        var result = _builder.Build(null, null, null);

        Assert.Contains("[EULA]", result);
        Assert.Contains("accepteula = Y", result);
        Assert.Contains("[sqlagent]", result);
        Assert.Contains("enabled = true", result);
        Assert.Contains("[telemetry]", result);
        Assert.Contains("customerfeedback = false", result);
    }

    [Fact]
    public void Build_WithDefaults_ContainsDefaultFilePaths()
    {
        var result = _builder.Build(null, null, null);

        Assert.Contains("[filelocation]", result);
        Assert.Contains("defaultdatadir = /var/opt/mssql/data", result);
        Assert.Contains("defaultlogdir = /var/opt/mssql/log", result);
        Assert.Contains("defaultbackupdir = /var/opt/mssql/backup", result);
    }

    [Fact]
    public void Build_WithoutDedicatedSysDbVolume_PutsMasterOnPersistentDataVolume()
    {
        // Regression: master must NOT default to /var/opt/mssql/sysdb, which is only
        // mounted when a dedicated sysDb volume is configured. Left there it lives on the
        // pod's ephemeral filesystem and the catalog is lost on every restart. With no
        // dedicated volume, master must co-locate on the always-persistent data volume.
        var result = _builder.Build(null, null, null);

        Assert.Contains("masterdatafile = /var/opt/mssql/data/master.mdf", result);
        Assert.Contains("masterlogfile = /var/opt/mssql/data/mastlog.ldf", result);
        Assert.DoesNotContain("/var/opt/mssql/sysdb", result);
    }

    [Fact]
    public void Build_WithDedicatedSysDbVolume_PutsMasterOnSysDbMount()
    {
        var storage = new StorageSpec { SysDb = new StorageVolumeSpec { Size = "5Gi" } };

        var result = _builder.Build(null, null, storage);

        Assert.Contains("masterdatafile = /var/opt/mssql/sysdb/master.mdf", result);
        Assert.Contains("masterlogfile = /var/opt/mssql/sysdb/mastlog.ldf", result);
    }

    [Fact]
    public void Build_WithCustomDataPath_KeepsMasterOnTheManagedVolume()
    {
        // defaultDataPath relocates NEW user databases; it must never move master.
        // It used to: the config hash changed, the pod restarted immediately, and SQL Server came up
        // on a master.mdf that did not exist — initialising a BLANK master (logins, jobs and the
        // database catalogue gone) or CrashLooping.
        var config = new SqlConfigurationSpec
        {
            ServerSettings = new ServerSettingsSpec { DefaultDataPath = "/var/opt/mssql/data/userdbs" }
        };

        var result = _builder.Build(config, null, null);

        Assert.Contains("defaultdatadir = /var/opt/mssql/data/userdbs", result);
        Assert.Contains("masterdatafile = /var/opt/mssql/data/master.mdf", result);
        Assert.Contains("masterlogfile = /var/opt/mssql/data/mastlog.ldf", result);
    }

    [Fact]
    public void Build_WithMemorySpec_ContainsMemoryLimit()
    {
        var memory = new MemorySpec { Total = "16Gi", SystemReserve = "2Gi" };

        var result = _builder.Build(null, memory, null);

        Assert.Contains("[memory]", result);
        // 16Gi - 2Gi = 14336 MB
        Assert.Contains("memorylimitmb = 14336", result);
    }

    [Fact]
    public void Build_WithoutMemorySpec_DoesNotContainMemorySection()
    {
        var result = _builder.Build(null, null, null);

        Assert.DoesNotContain("[memory]", result);
        Assert.DoesNotContain("memorylimitmb", result);
    }

    [Fact]
    public void Build_WithStartupTraceFlags_ContainsTraceFlagSection()
    {
        var config = new SqlConfigurationSpec
        {
            TraceFlags = new TraceFlagSpec
            {
                Startup = [1117, 1118, 3226]
            }
        };

        var result = _builder.Build(config, null, null);

        Assert.Contains("[traceflag]", result);
        Assert.Contains("traceflag0 = 1117", result);
        Assert.Contains("traceflag1 = 1118", result);
        Assert.Contains("traceflag2 = 3226", result);
    }

    [Fact]
    public void Build_WithGlobalTraceFlagsOnly_DoesNotContainTraceFlagSection()
    {
        var config = new SqlConfigurationSpec
        {
            TraceFlags = new TraceFlagSpec
            {
                Global = [1117, 1118]  // Global flags are applied at runtime, not in mssql.conf
            }
        };

        var result = _builder.Build(config, null, null);

        Assert.DoesNotContain("[traceflag]", result);
    }

    [Fact]
    public void Build_WithCustomServerSettingPaths_UsesThosePaths()
    {
        var config = new SqlConfigurationSpec
        {
            ServerSettings = new ServerSettingsSpec
            {
                DefaultDataPath = "/custom/data",
                DefaultLogPath = "/custom/log",
                DefaultBackupPath = "/custom/backup"
            }
        };

        var result = _builder.Build(config, null, null);

        Assert.Contains("defaultdatadir = /custom/data", result);
        Assert.Contains("defaultlogdir = /custom/log", result);
        Assert.Contains("defaultbackupdir = /custom/backup", result);
    }

    [Fact]
    public void ComputeHash_SameContent_SameHash()
    {
        var content = "[EULA]\naccepteula = Y\n";
        var hash1 = _builder.ComputeHash(content);
        var hash2 = _builder.ComputeHash(content);

        Assert.Equal(hash2, hash1);
        Assert.Equal(64, hash1.Length); // SHA256 hex
    }

    [Fact]
    public void ComputeHash_DifferentContent_DifferentHash()
    {
        var hash1 = _builder.ComputeHash("[EULA]\naccepteula = Y\n");
        var hash2 = _builder.ComputeHash("[EULA]\naccepteula = N\n");

        Assert.NotEqual(hash2, hash1);
    }
}
