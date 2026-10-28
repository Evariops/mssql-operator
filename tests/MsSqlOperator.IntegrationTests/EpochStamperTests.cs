using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

/// <summary> on a real SQL Server: the mssqlop schema, the stamps and the applock.</summary>
[Collection(SqlServerCollection.Name)]
public class EpochStamperTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Schema_IsCreatedIdempotently()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        await MssqlopSchema.EnsureAsync(connection, CancellationToken.None);
        await MssqlopSchema.EnsureAsync(connection, CancellationToken.None);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM msdb.mssqlop.schema_version";
        Assert.Equal(MssqlopSchema.Version, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Enforce_AStaleEpochIsRefused_AndTheStampNeverGoesDown()
    {
        var scope = $"ag-{Guid.NewGuid():N}"[..20];
        var stamper = new SqlEpochStamper();
        await using var connection = await fixture.CreateConnectionAsync();

        Assert.Equal(0, await stamper.RunAsync(connection, scope, 7, "leader-a", enforce: true, _ => Task.CompletedTask, CancellationToken.None));
        var ran = false;
        var stale = await Assert.ThrowsAsync<StaleEpochException>(() =>
            stamper.RunAsync(connection, scope, 5, "leader-b", enforce: true, _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(ran);
        Assert.Equal(7, stale.Stamped);

        // Shadow: the stale author runs, the stamp stays at 7.
        Assert.Equal(7, await stamper.RunAsync(connection, scope, 5, "leader-b", enforce: false, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(7, await stamper.RunAsync(connection, scope, 8, "leader-c", enforce: true, _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task TwoConcurrentCommandsOnOneScope_AreSerialized()
    {
        // acceptance: two topology commands on the same replica never overlap.
        var scope = $"ag-{Guid.NewGuid():N}"[..20];
        var stamper = new SqlEpochStamper();
        await using var a = await fixture.CreateConnectionAsync();
        await using var b = await fixture.CreateConnectionAsync();
        var inside = 0;
        var overlap = false;

        async Task Command(CancellationToken ct)
        {
            if (Interlocked.Increment(ref inside) > 1) overlap = true;
            await Task.Delay(1500, ct);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(
            stamper.RunAsync(a, scope, 1, "a", true, Command, CancellationToken.None),
            stamper.RunAsync(b, scope, 1, "b", true, Command, CancellationToken.None));

        Assert.False(overlap);
    }
}
