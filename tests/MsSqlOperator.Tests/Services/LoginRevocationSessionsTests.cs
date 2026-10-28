using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The sessions of a revoked login are killed once per revocation and per server.</summary>
public class LoginRevocationSessionsTests
{
    private readonly FakeSqlUserManager _users = new() { LoginExists = _ => true };
    private static readonly ResolvedTarget Target = new(TargetKinds.Instance, "sql", "db", "sql.db", 1433, "pw", null);

    private Task<RevocationOutcome> RevokeAsync(IReadOnlyCollection<string>? killedOn)
        => LoginRevocation.RevokeAsync(Target, "app", drop: false, terminateSessions: true, new FakeSqlConnectionFactory(),
            new FakeKubernetesClient(), _users, new FakeSqlServerManager(), NullLogger.Instance, CancellationToken.None, sessionsKilledOn: killedOn);

    [Fact]
    public async Task FirstPass_KillsAndSaysWhere_ThenNotAgain()
    {
        var first = await RevokeAsync(null);
        Assert.Equal(["SqlInstance/sql"], first.Killed);
        Assert.Single(_users.KillSessionsCalls);

        var second = await RevokeAsync(first.Killed);
        Assert.Empty(second.Killed);
        Assert.Single(_users.KillSessionsCalls);
        Assert.Equal(2, _users.DisableLoginCalls.Count); // the login stays disabled at every pass
    }

    [Fact]
    public async Task KilledSessionsStillRollingBack_AreReported()
    {
        _users.RollbacksInProgress = 3;
        var outcome = await RevokeAsync(null);
        Assert.Equal([("SqlInstance/sql", 3)], outcome.RollingBack);
    }
}
