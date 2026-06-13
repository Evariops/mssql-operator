using FluentAssertions;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class SqlStoredProcedureManagerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task DeployAndDropProcedure()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var procName = $"usp_Test_{Guid.NewGuid():N}"[..30];
        var sql = $"CREATE OR ALTER PROCEDURE [dbo].[{procName}] AS SELECT 1 AS Result";

        // Deploy
        await manager.DeployProcedureAsync(connection, "master", sql);

        // Check exists
        var exists = await manager.ProcedureExistsAsync(connection, "master", "dbo", procName);
        exists.Should().BeTrue();

        // Drop
        await manager.DropProcedureAsync(connection, "master", "dbo", procName);
        exists = await manager.ProcedureExistsAsync(connection, "master", "dbo", procName);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSyntax_ValidSql_ReturnsTrue()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var valid = await manager.ValidateSyntaxAsync(connection, "master", "CREATE PROCEDURE dbo.usp_Valid AS SELECT 1");

        valid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSyntax_InvalidSql_ReturnsFalse()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var valid = await manager.ValidateSyntaxAsync(connection, "master", "CREATE PROCEDURE dbo.usp_Invalid AS INVALID SQL SYNTAX");

        valid.Should().BeFalse();
    }

    [Fact]
    public async Task DeployInTransaction_DeploysMultipleProcedures()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var proc1 = $"usp_Tx1_{Guid.NewGuid():N}"[..30];
        var proc2 = $"usp_Tx2_{Guid.NewGuid():N}"[..30];

        var statements = new List<string>
        {
            $"CREATE OR ALTER PROCEDURE [dbo].[{proc1}] AS SELECT 1",
            $"CREATE OR ALTER PROCEDURE [dbo].[{proc2}] AS SELECT 2"
        };

        await manager.DeployInTransactionAsync(connection, "master", statements);

        var exists1 = await manager.ProcedureExistsAsync(connection, "master", "dbo", proc1);
        var exists2 = await manager.ProcedureExistsAsync(connection, "master", "dbo", proc2);
        exists1.Should().BeTrue();
        exists2.Should().BeTrue();

        // Cleanup
        await manager.DropProcedureAsync(connection, "master", "dbo", proc1);
        await manager.DropProcedureAsync(connection, "master", "dbo", proc2);
    }
}
