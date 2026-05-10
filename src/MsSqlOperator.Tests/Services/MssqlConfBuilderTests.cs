using FluentAssertions;
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

        result.Should().Contain("[EULA]");
        result.Should().Contain("accepteula = Y");
        result.Should().Contain("[sqlagent]");
        result.Should().Contain("enabled = true");
        result.Should().Contain("[telemetry]");
        result.Should().Contain("customerfeedback = false");
    }

    [Fact]
    public void Build_WithDefaults_ContainsDefaultFilePaths()
    {
        var result = _builder.Build(null, null, null);

        result.Should().Contain("[filelocation]");
        result.Should().Contain("defaultdatadir = /var/opt/mssql/data");
        result.Should().Contain("defaultlogdir = /var/opt/mssql/log");
        result.Should().Contain("defaultbackupdir = /var/opt/mssql/backup");
        result.Should().Contain("masterdatafile = /var/opt/mssql/sysdb/master.mdf");
        result.Should().Contain("masterlogfile = /var/opt/mssql/sysdb/mastlog.ldf");
    }

    [Fact]
    public void Build_WithMemorySpec_ContainsMemoryLimit()
    {
        var memory = new MemorySpec { Total = "16Gi", SystemReserve = "2Gi" };

        var result = _builder.Build(null, memory, null);

        result.Should().Contain("[memory]");
        // 16Gi - 2Gi = 14336 MB
        result.Should().Contain("memorylimitmb = 14336");
    }

    [Fact]
    public void Build_WithoutMemorySpec_DoesNotContainMemorySection()
    {
        var result = _builder.Build(null, null, null);

        result.Should().NotContain("[memory]");
        result.Should().NotContain("memorylimitmb");
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

        result.Should().Contain("[traceflag]");
        result.Should().Contain("traceflag0 = 1117");
        result.Should().Contain("traceflag1 = 1118");
        result.Should().Contain("traceflag2 = 3226");
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

        result.Should().NotContain("[traceflag]");
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

        result.Should().Contain("defaultdatadir = /custom/data");
        result.Should().Contain("defaultlogdir = /custom/log");
        result.Should().Contain("defaultbackupdir = /custom/backup");
    }

    [Fact]
    public void ComputeHash_SameContent_SameHash()
    {
        var content = "[EULA]\naccepteula = Y\n";
        var hash1 = _builder.ComputeHash(content);
        var hash2 = _builder.ComputeHash(content);

        hash1.Should().Be(hash2);
        hash1.Should().HaveLength(64); // SHA256 hex
    }

    [Fact]
    public void ComputeHash_DifferentContent_DifferentHash()
    {
        var hash1 = _builder.ComputeHash("[EULA]\naccepteula = Y\n");
        var hash2 = _builder.ComputeHash("[EULA]\naccepteula = N\n");

        hash1.Should().NotBe(hash2);
    }
}
