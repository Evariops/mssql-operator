using FluentAssertions;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.IntegrationTests;

[Collection("SqlServer")]
public class SqlUserManagerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task LoginLifecycle_CreateCheckDrop()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlUserManager();

        var loginName = $"test_login_{Guid.NewGuid():N}"[..30];

        // Create
        await manager.CreateLoginAsync(connection, loginName, "T3stP@ss!", checkPolicy: false);
        var exists = await manager.LoginExistsAsync(connection, loginName);
        exists.Should().BeTrue();

        // Get SID
        var sid = await manager.GetLoginSidAsync(connection, loginName);
        sid.Should().NotBeNull();

        // Drop
        await manager.DropLoginAsync(connection, loginName);
        exists = await manager.LoginExistsAsync(connection, loginName);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DatabaseUserLifecycle_CreateRoleDrop()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var userManager = new SqlUserManager();
        var serverManager = new SqlServerManager();

        var loginName = $"test_dbuser_{Guid.NewGuid():N}"[..30];
        var dbName = "master"; // Use master to avoid creating a test DB

        // Create login first
        await userManager.CreateLoginAsync(connection, loginName, "T3stP@ss!", checkPolicy: false);

        // Create database user
        await userManager.CreateDatabaseUserAsync(connection, dbName, loginName, loginName);
        var userExists = await userManager.DatabaseUserExistsAsync(connection, dbName, loginName);
        userExists.Should().BeTrue();

        // Add to role
        await userManager.AddRoleMemberAsync(connection, dbName, loginName, "db_datareader");

        // Drop user and login
        await userManager.DropDatabaseUserAsync(connection, dbName, loginName);
        userExists = await userManager.DatabaseUserExistsAsync(connection, dbName, loginName);
        userExists.Should().BeFalse();

        await userManager.DropLoginAsync(connection, loginName);
    }

    [Fact]
    public async Task GrantServerPermission_DoesNotThrow()
    {
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlUserManager();

        var loginName = $"test_perm_{Guid.NewGuid():N}"[..30];
        await manager.CreateLoginAsync(connection, loginName, "T3stP@ss!", checkPolicy: false);

        var act = async () => await manager.GrantServerPermissionAsync(connection, loginName, "VIEW SERVER STATE");
        await act.Should().NotThrowAsync();

        await manager.DropLoginAsync(connection, loginName);
    }

    [Fact]
    public async Task AlterLoginPassword_Succeeds()
    {
        // Regression: CREATE/ALTER LOGIN ... WITH PASSWORD took the password as a query
        // parameter, which T-SQL rejects ("Incorrect syntax near '@password'"). Both must
        // succeed with an escaped literal.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlUserManager();
        var loginName = $"test_alter_{Guid.NewGuid():N}"[..28];

        await manager.CreateLoginAsync(connection, loginName, "T3stP@ss!", checkPolicy: false);
        var act = async () => await manager.AlterLoginPasswordAsync(connection, loginName, "N3wP@ssw0rd!");
        await act.Should().NotThrowAsync();

        await manager.DropLoginAsync(connection, loginName);
    }

    [Fact]
    public async Task CreateLogin_WithApostropheInPassword_IsEscaped()
    {
        // The password literal must be escaped: an embedded apostrophe must not break the
        // statement or allow injection.
        await using var connection = await fixture.CreateConnectionAsync();
        var manager = new SqlUserManager();
        var loginName = $"test_quote_{Guid.NewGuid():N}"[..28];

        var act = async () => await manager.CreateLoginAsync(connection, loginName, "p@ss'w0rd!\"x", checkPolicy: false);
        await act.Should().NotThrowAsync();
        (await manager.LoginExistsAsync(connection, loginName)).Should().BeTrue();

        await manager.DropLoginAsync(connection, loginName);
    }
}
