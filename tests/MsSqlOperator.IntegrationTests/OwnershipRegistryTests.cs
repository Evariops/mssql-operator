using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

/// <summary> on a real SQL Server: what the operator owns, in msdb.mssqlop.</summary>
[Collection(SqlServerCollection.Name)]
public class OwnershipRegistryTests(SqlServerFixture fixture)
{
    private static ResourceOwner Owner(string name) => new("SqlUser", "db", name, Guid.NewGuid().ToString());

    [Fact]
    public async Task Principal_TheFirstClaimWins_AnotherOwnerSeesIt()
    {
        var registry = new SqlOwnershipRegistry();
        await using var connection = await fixture.CreateConnectionAsync();
        var login = $"app_{Guid.NewGuid():N}"[..20];
        var first = Owner("first");
        var second = Owner("second");

        var claimed = await registry.ClaimPrincipalAsync(connection, login, first);
        var refused = await registry.ClaimPrincipalAsync(connection, login, second);

        Assert.Equal(first, claimed.Owner);
        Assert.Equal(first, refused.Owner); // The second one is told, not handed the login
    }

    [Fact]
    public async Task Principal_TakeOver_OnlyFromTheExpectedFormerOwner_ThenGrantsAndRelease()
    {
        var registry = new SqlOwnershipRegistry();
        await using var connection = await fixture.CreateConnectionAsync();
        var login = $"app_{Guid.NewGuid():N}"[..20];
        var gone = Owner("gone");
        var next = Owner("next");
        await registry.ClaimPrincipalAsync(connection, login, gone);

        Assert.False(await registry.TakeOverPrincipalAsync(connection, login, "not-the-owner", next));
        Assert.True(await registry.TakeOverPrincipalAsync(connection, login, gone.Uid, next));

        await registry.RecordServerGrantsAsync(connection, login, next.Uid, """{"roles":["dbcreator"]}""");
        Assert.Equal("""{"roles":["dbcreator"]}""", (await registry.GetPrincipalAsync(connection, login))!.ServerGrants);

        await registry.ReleasePrincipalAsync(connection, login, gone.Uid); // not the owner any more: no effect
        Assert.NotNull(await registry.GetPrincipalAsync(connection, login));
        await registry.ReleasePrincipalAsync(connection, login, next.Uid);
        Assert.Null(await registry.GetPrincipalAsync(connection, login));
    }

    [Fact]
    public async Task Setting_TheFirstOriginalValueWins_TraceFlagsAreOwned()
    {
        var registry = new SqlOwnershipRegistry();
        await using var connection = await fixture.CreateConnectionAsync();
        var owner = Owner("config") with { Kind = "SqlConfiguration" };
        var setting = $"test setting {Guid.NewGuid():N}"[..30];
        var flag = 900_000 + Random.Shared.Next(0, 99_999);

        await registry.RecordOriginalSettingAsync(connection, setting, 0, owner);
        await registry.RecordOriginalSettingAsync(connection, setting, 4, owner); // already changed: not the original
        await registry.RecordTraceFlagAsync(connection, flag, owner);

        Assert.Equal(0, Assert.Single(await registry.GetOwnedSettingsAsync(connection, owner.Uid)).OriginalValue);
        Assert.Equal([flag], await registry.GetOwnedTraceFlagsAsync(connection, owner.Uid));

        await registry.ForgetSettingAsync(connection, setting, owner.Uid);
        await registry.ForgetTraceFlagAsync(connection, flag, owner.Uid);
        Assert.Empty(await registry.GetOwnedSettingsAsync(connection, owner.Uid));
        Assert.Empty(await registry.GetOwnedTraceFlagsAsync(connection, owner.Uid));
    }
}
