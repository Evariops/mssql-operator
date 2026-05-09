using FluentAssertions;
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

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_DifferentSql_ReturnsDifferentHash()
    {
        var hash1 = _manager.ComputeHash("CREATE PROCEDURE dbo.usp_Test AS SELECT 1");
        var hash2 = _manager.ComputeHash("CREATE PROCEDURE dbo.usp_Test AS SELECT 2");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHash_ReturnsHexString()
    {
        var hash = _manager.ComputeHash("SELECT 1");

        hash.Should().MatchRegex("^sha256:[a-fA-F0-9]{64}$");
    }

    [Fact]
    public void ParseProcedureName_SimpleCreateProcedure_ExtractsName()
    {
        var sql = "CREATE PROCEDURE [dbo].[usp_GetCustomers] AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        result.Should().NotBeNull();
        result!.Value.schema.Should().Be("dbo");
        result!.Value.name.Should().Be("usp_GetCustomers");
    }

    [Fact]
    public void ParseProcedureName_CreateOrAlter_ExtractsName()
    {
        var sql = "CREATE OR ALTER PROCEDURE [sales].[usp_UpdateOrder] AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        result.Should().NotBeNull();
        result!.Value.schema.Should().Be("sales");
        result!.Value.name.Should().Be("usp_UpdateOrder");
    }

    [Fact]
    public void ParseProcedureName_NoSchema_DefaultsToDbo()
    {
        var sql = "CREATE PROCEDURE usp_Simple AS SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        result.Should().NotBeNull();
        result!.Value.schema.Should().Be("dbo");
        result!.Value.name.Should().Be("usp_Simple");
    }

    [Fact]
    public void ParseProcedureName_InvalidSql_ReturnsNull()
    {
        var sql = "SELECT 1";
        var result = _manager.ParseProcedureName(sql);

        result.Should().BeNull();
    }
}
