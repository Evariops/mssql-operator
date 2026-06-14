using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class SqlStoredProcedureManagerTests
{
    private readonly SqlStoredProcedureManager _manager = new();

    [Fact]
    public void ComputeHash_SameSql_ReturnsSameHash()
    {
        var sql = "CREATE PROCEDURE dbo.usp_Test AS SELECT 1";
        var hash1 = _manager.ComputeHash(sql);
        var hash2 = _manager.ComputeHash(sql);

        Assert.Equal(hash2, hash1);
    }

    [Fact]
    public void ComputeHash_DifferentSql_ReturnsDifferentHash()
    {
        var hash1 = _manager.ComputeHash("CREATE PROCEDURE dbo.usp_Test AS SELECT 1");
        var hash2 = _manager.ComputeHash("CREATE PROCEDURE dbo.usp_Test AS SELECT 2");

        Assert.NotEqual(hash2, hash1);
    }

    [Fact]
    public void ComputeHash_ReturnsHexString()
    {
        var hash = _manager.ComputeHash("SELECT 1");

        Assert.Matches("^sha256:[a-fA-F0-9]{64}$", hash);
    }

    [Fact]
    public void ParseProcedureName_SimpleCreateProcedure_ExtractsName()
    {
        var sql = "CREATE PROCEDURE [dbo].[usp_GetCustomers] AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        Assert.NotNull(result);
        Assert.Equal("dbo", result!.Value.schema);
        Assert.Equal("usp_GetCustomers", result!.Value.name);
    }

    [Fact]
    public void ParseProcedureName_CreateOrAlter_ExtractsName()
    {
        var sql = "CREATE OR ALTER PROCEDURE [sales].[usp_UpdateOrder] AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        Assert.NotNull(result);
        Assert.Equal("sales", result!.Value.schema);
        Assert.Equal("usp_UpdateOrder", result!.Value.name);
    }

    [Fact]
    public void ParseProcedureName_NoSchema_DefaultsToDbo()
    {
        var sql = "CREATE PROCEDURE usp_Simple AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        Assert.NotNull(result);
        Assert.Equal("dbo", result!.Value.schema);
        Assert.Equal("usp_Simple", result!.Value.name);
    }

    [Fact]
    public void ParseProcedureName_InvalidSql_ReturnsNull()
    {
        var sql = "SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        Assert.Null(result);
    }
}
