using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>Profiles, error taxonomy, execution helper.</summary>
public class SqlExecutionLayerTests
{
    [Theory]
    [InlineData(1222, SqlErrorKind.LockTimeout)]
    [InlineData(1205, SqlErrorKind.Deadlock)]
    [InlineData(-2, SqlErrorKind.CommandTimeout)]
    [InlineData(18456, SqlErrorKind.LoginFailed)]
    [InlineData(229, SqlErrorKind.PermissionDenied)]
    [InlineData(15025, SqlErrorKind.AlreadyExists)]
    [InlineData(15151, SqlErrorKind.NotFound)]
    [InlineData(976, SqlErrorKind.AgUnavailable)]
    [InlineData(40613, SqlErrorKind.Transient)]
    [InlineData(50000, SqlErrorKind.Other)]
    public void Classify_ByNumber(int number, SqlErrorKind kind)
    {
        Assert.Equal(kind, SqlErrors.Classify(number));
        Assert.Equal(kind, SqlErrors.Classify(SqlExceptions.WithNumbers(number)));
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(1205, true)]
    [InlineData(4060, true)]
    [InlineData(10054, true)]
    [InlineData(1222, false)] // a lock timeout is retried only by an idempotent operation (SqlExecution)
    [InlineData(18456, false)]
    public void TransientSqlErrors_KeepsItsFormerSet(int number, bool transient)
        => Assert.Equal(transient, TransientSqlErrors.IsTransient(SqlExceptions.WithNumbers(number)));

    [Fact]
    public void ConnectionString_NamesTheOperatorAndItsComponent()
    {
        var factory = new SqlConnectionFactory(Options.Create(new SqlConnectionOptions()));

        var builder = new SqlConnectionStringBuilder(factory.BuildConnectionString("h", 1433, "pw", component: "SqlUserController"));

        Assert.Equal("mssql-operator/SqlUserController", builder.ApplicationName);
        Assert.Equal("SqlAvailabilityGroupController", SqlConnectionFactory.ComponentOf("/src/Controllers/SqlAvailabilityGroupController.cs"));
    }

    [Fact]
    public void DdlProfile_FailsFastOnLocksAndLosesDeadlocks()
    {
        var ddl = SqlProfileSettings.For(SqlProfile.Ddl, new SqlConnectionOptions());

        Assert.Equal((5, 2, 30), (ddl.ConnectTimeoutSeconds, ddl.OpenRetries, ddl.CommandTimeoutSeconds));
        Assert.Contains("LOCK_TIMEOUT 3000", ddl.SessionBatch);
        Assert.Contains("DEADLOCK_PRIORITY LOW", ddl.SessionBatch);
        Assert.Contains("XACT_ABORT ON", ddl.SessionBatch);
        Assert.Null(SqlProfileSettings.For(SqlProfile.Probe, new SqlConnectionOptions()).SessionBatch);
    }

    [Fact]
    public async Task RunAsync_SqlErrorAfterTheCallerCancelled_IsACancellation()
    {
        // SqlClient reports a cancelled command as a SqlException; counted as a failure, it became a
        // business error (event, condition) for what was a shutdown or a leadership loss.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SqlExecution.RunAsync(_ => Task.FromException(SqlExceptions.WithNumbers(0)), cts.Token));
    }

    [Fact]
    public async Task RunAsync_LockTimeout_RetriedOnlyWhenIdempotent()
    {
        var calls = 0;
        Task Op(CancellationToken _) => ++calls < 2 ? Task.FromException(SqlExceptions.WithNumbers(1222)) : Task.CompletedTask;

        await SqlExecution.RunAsync(Op, CancellationToken.None, idempotent: true);
        Assert.Equal(2, calls);

        calls = 0;
        await Assert.ThrowsAsync<SqlException>(() => SqlExecution.RunAsync(Op, CancellationToken.None));
        Assert.Equal(1, calls);
    }
}
