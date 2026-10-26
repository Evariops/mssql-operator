using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>Only procedure definitions are ever executed.</summary>
public class ProcedureScriptTests
{
    [Fact]
    public void SingleProcedure_IsParsed()
    {
        var proc = Assert.Single(ProcedureScript.Parse("CREATE OR ALTER PROCEDURE [sales].[usp_Get] AS SELECT 1"));
        Assert.Equal(("sales", "usp_Get"), (proc.Schema, proc.Name));
    }

    [Fact]
    public void UnqualifiedName_DefaultsToDbo()
        => Assert.Equal("dbo", Assert.Single(ProcedureScript.Parse("create proc usp_X as select 1")).Schema);

    [Fact]
    public void GoBatches_AndSessionSettings_AreSplit()
    {
        const string script = """
            SET ANSI_NULLS ON
            GO
            SET QUOTED_IDENTIFIER OFF
            GO
            /* header comment */
            CREATE PROCEDURE dbo.usp_A AS SELECT 'go'
            go
            -- second one
            CREATE PROCEDURE dbo.usp_B AS SELECT 2
            """;

        var procs = ProcedureScript.Parse(script);

        Assert.Equal(["usp_A", "usp_B"], procs.Select(p => p.Name));
        Assert.All(procs, p => Assert.False(p.QuotedIdentifier));
        Assert.All(procs, p => Assert.DoesNotContain("\nGO", p.Batch, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GoInsideACommentOrString_IsNotASeparator()
    {
        const string script = "CREATE PROCEDURE dbo.usp_A AS\n/*\nGO\n*/\nSELECT '\nGO\n'";
        Assert.Single(ProcedureScript.Parse(script));
    }

    [Theory]
    [InlineData("EXEC sp_configure 'xp_cmdshell', 1; RECONFIGURE;")]
    [InlineData("ALTER SERVER ROLE sysadmin ADD MEMBER [x]")]
    [InlineData("DROP TABLE dbo.Orders")]
    [InlineData("CREATE PROCEDURE dbo.usp_A AS SELECT 1\nGO\nEXEC xp_cmdshell 'id'")]
    [InlineData("SET ANSI_NULLS ON")]
    [InlineData("CREATE PROCEDURE dbo.usp_A AS SELECT 1\nGO 5")]
    public void AnythingButProcedureDefinitions_IsRefused(string script)
        => Assert.Throws<ProcedureScriptException>(() => ProcedureScript.Parse(script));

    [Fact]
    public void Hash_DependsOnSettingsAndBody()
    {
        var a = ProcedureScript.Parse("CREATE PROCEDURE dbo.p AS SELECT 1")[0];
        var b = ProcedureScript.Parse("SET QUOTED_IDENTIFIER OFF\nGO\nCREATE PROCEDURE dbo.p AS SELECT 1")[0];
        var c = ProcedureScript.Parse("CREATE PROCEDURE dbo.p AS SELECT 2")[0];
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.Equal(a.Hash, ProcedureScript.Parse("CREATE PROCEDURE dbo.p AS SELECT 1\r\n")[0].Hash);
    }

    [Fact]
    public void Marker_RoundTrips()
    {
        var marker = ProcedureMarker.Parse(ProcedureMarker.Prefix("ns/procs", "sha256:abc") + "DEADBEEF");
        Assert.NotNull(marker);
        Assert.True(marker!.IsCurrent("ns/procs", "sha256:abc", "deadbeef"));
        Assert.False(marker.IsCurrent("ns/other", "sha256:abc", "deadbeef"));
        Assert.False(marker.IsCurrent("ns/procs", "sha256:abc", "CAFE"));
        Assert.Null(ProcedureMarker.Parse("something else"));
    }
}
