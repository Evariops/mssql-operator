using Microsoft.Data.SqlClient;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

/// <summary>SqlDatabase on a real engine: creation, ownership tag, options, files, drop.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlDatabaseManagerTests(SqlServerFixture fixture)
{
    private readonly SqlDatabaseManager _sut = new();

    private static string NewName() => $"sqldb_{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Create_WithACollation_ThenReadBack()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();

        Assert.True(await _sut.CollationExistsAsync(conn, "French_CI_AS"));
        Assert.False(await _sut.CollationExistsAsync(conn, "No_Such_Collation"));
        await _sut.CreateDatabaseAsync(conn, name, "French_CI_AS");
        await _sut.CreateDatabaseAsync(conn, name, "French_CI_AS"); // idempotent

        var db = await _sut.GetDatabaseAsync(conn, name);
        Assert.NotNull(db);
        Assert.Equal("French_CI_AS", db.Collation);
        Assert.Equal("ONLINE", db.State);
        Assert.False(db.InAvailabilityGroup);

        await _sut.DropDatabaseAsync(conn, name);
        Assert.Null(await _sut.GetDatabaseAsync(conn, name));
    }

    [Fact]
    public async Task UserObjects_NoneInANewDatabase_OneOnceATableIsCreated()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);
        await _sut.SetOwnerTagAsync(conn, name, "SqlDatabase/sql/a/uid-1"); // an extended property is not an object

        Assert.False(await _sut.HasUserObjectsAsync(conn, name));
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE {SqlQuoting.QuoteIdentifier(name)}.dbo.t (id int)";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(await _sut.HasUserObjectsAsync(conn, name));

        await _sut.DropDatabaseAsync(conn, name);
    }

    [Fact]
    public async Task OwnerTag_RecoveryModel_CompatibilityLevel_Owner()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);

        Assert.Null(await _sut.GetOwnerTagAsync(conn, name));
        await _sut.SetOwnerTagAsync(conn, name, "SqlDatabase/sql/a/uid-1");
        await _sut.SetOwnerTagAsync(conn, name, "SqlDatabase/sql/a/uid-2");
        Assert.Equal("SqlDatabase/sql/a/uid-2", await _sut.GetOwnerTagAsync(conn, name));

        await _sut.SetRecoveryModelAsync(conn, name, "BULK_LOGGED");
        await _sut.SetCompatibilityLevelAsync(conn, name, 150);
        var login = $"own_{Guid.NewGuid():N}"[..16];
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE LOGIN {SqlQuoting.QuoteIdentifier(login)} WITH PASSWORD = {SqlQuoting.QuoteNStringLiteral($"Pw-{Guid.NewGuid():N}-A1!")}";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(await _sut.LoginExistsAsync(conn, login));
        await _sut.SetOwnerAsync(conn, name, login);

        var db = (await _sut.GetDatabaseAsync(conn, name))!;
        Assert.Equal("BULK_LOGGED", db.RecoveryModel);
        Assert.Equal(150, db.CompatibilityLevel);
        Assert.Equal(login, db.Owner);

        await _sut.DropDatabaseAsync(conn, name);
    }

    [Fact]
    public async Task Files_GrowAndChangeTheirGrowth()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);

        var files = await _sut.GetFilesAsync(conn, name);
        var data = files.Single(f => f.Type == 0);
        var log = files.Single(f => f.Type == 1);
        await _sut.ModifyFileAsync(conn, name, data.LogicalName, 64L << 20, 32L << 20, false, 1L << 30);
        await _sut.ModifyFileAsync(conn, name, log.LogicalName, null, 10, true, -1);

        files = await _sut.GetFilesAsync(conn, name);
        data = files.Single(f => f.Type == 0);
        log = files.Single(f => f.Type == 1);
        Assert.Equal(64L << 20, data.SizeBytes);
        Assert.Equal(32L << 20, data.Growth);
        Assert.Equal(1L << 30, data.MaxSizeBytes);
        Assert.True(log.GrowthIsPercent);
        Assert.Equal(10, log.Growth);
        Assert.Equal(-1, log.MaxSizeBytes);

        await _sut.DropDatabaseAsync(conn, name);
    }

    [Fact]
    public async Task Files_ReportTheFreeSpaceOfTheirVolume_AndAFailedGrowthGivesItBack()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);
        var data = (await _sut.GetFilesAsync(conn, name)).Single(f => f.Type == 0);
        Assert.True(data.AvailableBytes > 0);
        await _sut.ModifyFileAsync(conn, name, data.LogicalName, 64L << 20, null, false, null);

        await _sut.ReleaseFailedGrowthAsync(conn, name, data.LogicalName, 64L << 20);

        Assert.Equal(64L << 20, (await _sut.GetFilesAsync(conn, name)).Single(f => f.Type == 0).SizeBytes);
        await _sut.DropDatabaseAsync(conn, name);
    }

    [Fact]
    public async Task DropOrphanCopy_BacksTheDatabaseUp_ThenDropsIt()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);

        var path = await _sut.DropOrphanCopyAsync(conn, name, "/tmp/mssqlop-orphans", $"{name}.bak");

        Assert.Null(await _sut.GetDatabaseAsync(conn, name));
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "RESTORE HEADERONLY FROM DISK = @path";
        cmd.Parameters.AddWithValue("@path", path);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(name, reader.GetString(reader.GetOrdinal("DatabaseName")));
    }

    [Fact]
    public async Task Drop_ClosesTheSessionsOfTheDatabase()
    {
        await using var conn = await fixture.CreateConnectionAsync();
        var name = NewName();
        await _sut.CreateDatabaseAsync(conn, name, null);
        await using var user = await fixture.CreateConnectionAsync(name);

        await _sut.DropDatabaseAsync(conn, name);

        Assert.Null(await _sut.GetDatabaseAsync(conn, name));
        await using var cmd = user.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await Assert.ThrowsAnyAsync<SqlException>(() => cmd.ExecuteScalarAsync());
    }
}
