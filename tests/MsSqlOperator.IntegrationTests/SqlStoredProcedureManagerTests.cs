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
        Assert.True(exists);

        // Drop
        await manager.DropProcedureAsync(connection, "master", "dbo", procName);
        exists = await manager.ProcedureExistsAsync(connection, "master", "dbo", procName);
        Assert.False(exists);
    }

    [Fact]
    public async Task ValidateSyntax_ValidSql_ReturnsTrue()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var valid = await manager.ValidateSyntaxAsync(connection, "master", "CREATE PROCEDURE dbo.usp_Valid AS SELECT 1");

        Assert.True(valid);
    }

    [Fact]
    public async Task ValidateSyntax_InvalidSql_ReturnsFalse()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlStoredProcedureManager();

        var valid = await manager.ValidateSyntaxAsync(connection, "master", "CREATE PROCEDURE dbo.usp_Invalid AS INVALID SQL SYNTAX");

        Assert.False(valid);
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
        Assert.True(exists1);
        Assert.True(exists2);

        // Cleanup
        await manager.DropProcedureAsync(connection, "master", "dbo", proc1);
        await manager.DropProcedureAsync(connection, "master", "dbo", proc2);
    }

    [Fact]
    public async Task DeployInTransaction_ProcedureHeldByAnotherSession_DeferredWithinSeconds()
    {
        // acceptance: a session holding a lock on the procedure makes the deployment give up in
        // about 3 s (LOCK_TIMEOUT) instead of queueing every caller of the procedure behind it.
        var manager = new SqlStoredProcedureManager();
        var procName = $"usp_Lk_{Guid.NewGuid():N}"[..30];
        await using var holder = await fixture.CreateConnectionAsync();
        await manager.DeployProcedureAsync(holder, "master", $"CREATE OR ALTER PROCEDURE [dbo].[{procName}] AS SELECT 1");
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = tx;
            hold.CommandText = $"ALTER PROCEDURE [dbo].[{procName}] AS SELECT 2";
            await hold.ExecuteNonQueryAsync();
        }

        await using var deployer = await fixture.CreateConnectionAsync();
        var definition = ProcedureScript.Parse($"CREATE OR ALTER PROCEDURE [dbo].[{procName}] AS SELECT 3").Single();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var deferred = await manager.DeployInTransactionAsync(deployer, "master", [definition]);
        watch.Stop();

        Assert.Single(deferred);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        await tx.RollbackAsync();
        await manager.DropProcedureAsync(deployer, "master", "dbo", procName);
    }
}
