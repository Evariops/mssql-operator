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
            },
            // The operator only drops what it provisioned — appliedLoginName is the proof.
            Status = new SqlUserStatus { AppliedLoginName = "app_user" }
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
    public async Task FinalizeAsync_FailedTarget_CountsTheAttemptInTheStatus()
    {
        // The attempt counter used to be an annotation, written with a full update of the
        // user's object (GitOps drift, write conflicts).
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test-user", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] },
                Login = new LoginSpec { Name = "app_user" }
            },
            Status = new SqlUserStatus { AppliedLoginName = "app_user" }
        };
        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _connFactory.ConnectionForHost = _ => throw new InvalidOperationException("unreachable");

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, entity.Status.FinalizeAttempts!["my-sql"]);
        Assert.Single(_k8sClient.UpdateStatusCalls);
        Assert.Empty(_k8sClient.UpdateCalls);
    }

    private SqlUser OnAgWithAStoppedReplica(int previousAttempts = 0)
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test-user", NamespaceProperty = "default" },
            Spec = new SqlUserSpec { Target = new TargetSelector { AvailabilityGroups = ["my-ag"] }, Login = new LoginSpec { Name = "app_user" } },
            Status = new SqlUserStatus
            {
                AppliedLoginName = "app_user",
                FinalizeAttempts = previousAttempts > 0 ? new Dictionary<string, int> { ["my-ag"] = previousAttempts } : null,
            },
        };
        _targetResolver.Resolved = [new(TargetKinds.AvailabilityGroup, "my-ag", "default", "my-ag.default.svc.cluster.local", 1433, "P@ss!", 3)];
        _k8sClient.SetGet("my-ag", "default", new SqlAvailabilityGroup
        {
            Metadata = new V1ObjectMeta { Name = "my-ag", NamespaceProperty = "default" },
            Spec = new SqlAvailabilityGroupSpec { Replicas = new AgReplicaTopologySpec { Count = 3 } },
        });
        _userManager.LoginExists = n => n == "app_user";
        _connFactory.ConnectionForHost = h => h.StartsWith("my-ag-2.", StringComparison.Ordinal)
            ? throw new InvalidOperationException("unreachable")
            : new Microsoft.Data.SqlClient.SqlConnection($"Server={h}");
        return entity;
    }

    [Fact]
    public async Task FinalizeAsync_ReplicaStopped_LoginDisabledAndDroppedElsewhere_FinalizerKept()
    {
        // A stopped replica no longer holds back the revocation on the others, and the
        // finalizer is not released while the login survives there.
        var entity = OnAgWithAStoppedReplica();

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, _userManager.DisableLoginCalls.Count);
        Assert.Equal(2, _userManager.DropLoginCalls.Count);
        Assert.Equal(["SqlAvailabilityGroup/my-ag/my-ag-0", "SqlAvailabilityGroup/my-ag/my-ag-1"], entity.Status!.RevokedServers);
        Assert.DoesNotContain(entity.Status.Conditions ?? [], c => c.Type == "NeedsAttention" && c.Status == "True");
    }

    [Fact]
    public async Task FinalizeAsync_ReplicaStillStoppedAfterMaxAttempts_NeedsAttention_NoSilentSuccess()
    {
        var entity = OnAgWithAStoppedReplica(previousAttempts: 2);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("RevocationIncomplete", entity.Status!.Conditions!.Single(c => c.Type == "NeedsAttention").Reason);
        Assert.Contains("RevocationIncomplete", _eventPublisher.Reasons);
    }

    [Fact]
    public async Task FinalizeAsync_StoppedReplicaBack_RevocationCompletes()
    {
        var entity = OnAgWithAStoppedReplica();
        await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        _connFactory.ConnectionForHost = h => new Microsoft.Data.SqlClient.SqlConnection($"Server={h}");

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, entity.Status!.RevokedServers!.Count);
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
    public async Task FinalizeAsync_LoginNotProvisionedByOperator_LeavesItAlone()
    {
        // A pre-existing login that merely shares the name (a DBA's 'reporting' login)
        // must survive the deletion of a CR that never provisioned it — dropping it, with its database
        // users and a forced schema transfer to dbo, is an immediate production outage.
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test-user", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] },
                Login = new LoginSpec { Name = "reporting" }
            },
            Status = new SqlUserStatus() // never provisioned: no appliedLoginName
        };

        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _userManager.LoginExists = n => n == "reporting";

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Empty(_userManager.DropLoginCalls);
        Assert.Empty(_userManager.DropDatabaseUserCalls);
        Assert.Empty(_userManager.KillSessionsCalls);
        Assert.Contains(_eventPublisher.Calls, c => c.Reason == "CleanupSkippedNotOwned");
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
            },
            Status = new SqlUserStatus { AppliedLoginName = "multi_user" }
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
