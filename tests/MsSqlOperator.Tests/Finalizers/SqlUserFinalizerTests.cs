using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlUserFinalizerTests
{
    private readonly FakeKubernetesClient _k8sClient = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlUserManager _userManager = new();
    private readonly FakeSqlServerManager _serverManager = new();
    private readonly FakeTargetResolver _targetResolver = new();
    private readonly FakeEventPublisher _eventPublisher = new();
    private readonly SqlUserFinalizer _finalizer;

    public SqlUserFinalizerTests()
    {
        _finalizer = new SqlUserFinalizer(NullLogger<SqlUserFinalizer>.Instance, _k8sClient, _connFactory, _userManager, _serverManager, _targetResolver, _eventPublisher);
    }

    [Fact]
    public async Task FinalizeAsync_DropsUsersAndLogin()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test-user", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] },
                Login = new LoginSpec { Name = "app_user" }
            }
        };

        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _serverManager.DatabaseNames = ["AppDb", "LogDb"];
        _userManager.DatabaseUserExists = (_, user) => user == "app_user";
        _userManager.LoginExists = n => n == "app_user";

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Contains("app_user", _userManager.KillSessionsCalls);
        Assert.Single(_userManager.DropDatabaseUserCalls, c => c.Db == "AppDb" && c.User == "app_user");
        Assert.Single(_userManager.DropDatabaseUserCalls, c => c.Db == "LogDb" && c.User == "app_user");
        Assert.Single(_userManager.DropLoginCalls, l => l == "app_user");
    }

    [Fact]
    public async Task FinalizeAsync_NoLogin_ReturnsSuccess()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] }
            }
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        Assert.NotNull(result.Entity);
    }

    [Fact]
    public async Task FinalizeAsync_NoTargetsResolved_ReturnsSuccess()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["missing"] },
                Login = new LoginSpec { Name = "user1" }
            }
        };

        _targetResolver.Resolved = [];

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        Assert.NotNull(result.Entity);
    }

    [Fact]
    public async Task FinalizeAsync_MultiTarget_DropsOnAll()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["db1", "db2"] },
                Login = new LoginSpec { Name = "multi_user" }
            }
        };

        _targetResolver.Resolved =
        [
            new("SqlInstance", "db1", "default", "db1.default.svc.cluster.local", 1433, "pass", null),
            new("SqlInstance", "db2", "default", "db2.default.svc.cluster.local", 1433, "pass", null)
        ];
        _serverManager.DatabaseNames = [];
        _userManager.LoginExists = n => n == "multi_user";

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.True(_userManager.KillSessionsCalls.Count(s => s == "multi_user") >= 2);
        Assert.Equal(2, _userManager.DropLoginCalls.Count(l => l == "multi_user"));
    }
}
