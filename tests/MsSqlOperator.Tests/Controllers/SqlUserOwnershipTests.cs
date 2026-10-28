using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

/// <summary>The ownership of a login, recorded in the msdb of each server.</summary>
public class SqlUserOwnershipTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connections = new();
    private readonly FakeSqlUserManager _users = new();
    private readonly FakeTargetResolver _resolver = new();
    private readonly FakeEventPublisher _events = new();
    private readonly FakeOwnershipRegistry _ownership = new();

    public SqlUserOwnershipTests()
    {
        _resolver.Resolved = [new ResolvedTarget(TargetKinds.Instance, "sql", "db", "sql.db.svc", 1433, "sa-pwd", null)];
        _k8s.SetGet("app-pwd", "db", new V1Secret { Data = new Dictionary<string, byte[]> { ["password"] = "Secret#1"u8.ToArray() } });
    }

    private SqlUserController Controller() => new(NullLogger<SqlUserController>.Instance, _k8s, _connections, _users,
        new FakeSqlServerManager(), new DatabaseFilter(), _resolver, _events, new OperatorMetrics(), ownership: _ownership);

    private static SqlUser User(string name, string uid, List<string>? roles = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "db", Uid = uid },
        Spec = new SqlUserSpec
        {
            Target = new TargetSelector { Instances = ["sql"] },
            Login = new LoginSpec { Name = "app", PasswordSecretRef = new SecretReference { Name = "app-pwd", Key = "password" } },
            ServerRoles = roles,
        },
    };

    private void Owned(string name, string uid, string? grants = null)
        => _ownership.Principals[("sql.db.svc", "app")] = new OwnedPrincipal("app", new ResourceOwner("SqlUser", "db", name, uid), grants);

    [Fact]
    public async Task LoginOfAnotherLivingSqlUser_IsRefused_ItsPasswordUntouched()
    {
        Owned("first", "uid-1");
        _k8s.SetGet("first", "db", User("first", "uid-1"));
        _users.LoginExists = _ => true;
        _users.PasswordMatches = (_, _) => false;

        var result = await Controller().ReconcileAsync(User("second", "uid-2"), CancellationToken.None);

        Assert.Empty(_users.AlterLoginPasswordCalls);
        Assert.Contains(result.Entity!.Status!.Conditions!, c => c is { Type: "LoginOwnedByAnotherResource", Status: "True" });
        Assert.Contains("LoginOwnedByAnotherResource", _events.Reasons);
    }

    [Fact]
    public async Task RecreatedSqlUser_TakesTheLoginOver_AndRevokesWhatWasRecorded()
    {
        // GitOps recreated the CR — new UID, empty status. The grants recorded in SQL by the former
        // one still tell what to revoke.
        Owned("app-user", "uid-old", new ServerGrants(["dbcreator"], []).ToJson());
        _users.LoginExists = _ => true;

        await Controller().ReconcileAsync(User("app-user", "uid-new"), CancellationToken.None);

        Assert.Equal("uid-new", _ownership.Principals[("sql.db.svc", "app")].Owner.Uid);
        Assert.Contains(("app", "dbcreator"), _users.DropServerRoleMemberCalls);
        Assert.Contains("LoginOwnershipTakenOver", _events.Reasons);
    }

    [Fact]
    public async Task Grants_AreRecordedOnTheServer()
    {
        // A user-defined server role: the fixed ones need the target's opt-in.
        await Controller().ReconcileAsync(User("app-user", "uid-1", ["app_monitoring"]), CancellationToken.None);

        Assert.Equal(["app_monitoring"], ServerGrants.FromJson(_ownership.Principals[("sql.db.svc", "app")].ServerGrants).Roles);
    }

    [Fact]
    public async Task Finalizer_LeavesTheLoginOfAnotherOwner()
    {
        Owned("first", "uid-1");
        _users.LoginExists = _ => true;
        var second = User("second", "uid-2");
        second.Status = new SqlUserStatus { AppliedLoginName = "app" };
        var finalizer = new SqlUserFinalizer(NullLogger<SqlUserFinalizer>.Instance, _k8s, _connections, _users,
            new FakeSqlServerManager(), _resolver, _events, ownership: _ownership);

        await finalizer.FinalizeAsync(second, CancellationToken.None);

        Assert.Empty(_users.DropLoginCalls);
        Assert.Empty(_users.DisableLoginCalls);
    }
}
