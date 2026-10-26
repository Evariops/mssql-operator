using System.Text;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>The least-privilege stored-procedure deployer login lifecycle.</summary>
public class ProcDeployerLoginManagerTests
{
    private const string LoginName = "mssqlop_proc_deployer";
    private const string LegacyHashAnnotation = "mssql-operator.evariops.com/proc-deployer-hash";

    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlUserManager _users = new();
    private readonly FakeSqlServerManager _sqlServer = new();
    private readonly ProcDeployerLoginManager _manager;

    public ProcDeployerLoginManagerTests()
    {
        _manager = new ProcDeployerLoginManager(NullLogger<ProcDeployerLoginManager>.Instance, _k8s, _connFactory, _users, _sqlServer);
        _k8s.SetGet("inst", "default", new SqlInstance { Metadata = new V1ObjectMeta { Name = "inst", Uid = "uid-1" } });
    }

    // A distinct name per test: leases are serialized per target in-process.
    private static ResolvedTarget Instance(string name = "inst") =>
        new("SqlInstance", name, "default", $"{name}.default.svc.cluster.local", 1433, "saPwd!", null);

    [Fact]
    public async Task AcquireAsync_CreatesLoginUserAndRole_WhenAbsent()
    {
        var sa = new Microsoft.Data.SqlClient.SqlConnection();
        await using var lease = await _manager.AcquireAsync(Instance(), sa, ["AppDb"], CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(lease.Password));
        var login = Assert.Single(_users.CreateLoginCalls);
        Assert.Equal(LoginName, login.Login);
        Assert.Equal(lease.Password, login.Pwd);
        Assert.Null(login.DefaultDb);
        Assert.True(login.CheckPolicy);
        Assert.False(login.CheckExpiration);
        Assert.Null(login.Sid);
        Assert.Equal(("AppDb", LoginName, LoginName), Assert.Single(_users.CreateDatabaseUserCalls));
        Assert.Equal(("AppDb", LoginName, "db_ddladmin"), Assert.Single(_users.AddRoleMemberCalls));
        Assert.Empty(_users.AlterLoginPasswordCalls);
    }

    [Fact]
    public async Task Lease_RevokesDdlAdmin_WhenDisposed()
    {
        // db_ddladmin is held for the deployment only, never left behind.
        var lease = await _manager.AcquireAsync(Instance("inst-release"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb", "Other"], CancellationToken.None);
        Assert.Empty(_users.DropRoleMemberCalls);

        await lease.DisposeAsync();

        Assert.Equal(
            [("AppDb", LoginName, "db_ddladmin"), ("Other", LoginName, "db_ddladmin")],
            _users.DropRoleMemberCalls);
    }

    [Fact]
    public async Task AcquireAsync_RevokesStaleMembershipsOnOtherDatabases_Once()
    {
        // Memberships left by earlier versions on databases no longer targeted are dropped.
        _sqlServer.DatabaseNames = ["AppDb", "OldDb"];
        _users.DatabaseUserExists = (_, _) => true;
        _users.DatabaseRoleMemberships = (_, _) => ["db_ddladmin"];

        await (await _manager.AcquireAsync(Instance("inst-legacy"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None)).DisposeAsync();
        await (await _manager.AcquireAsync(Instance("inst-legacy"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None)).DisposeAsync();

        Assert.Single(_users.DropRoleMemberCalls, c => c.Db == "OldDb");
    }

    [Fact]
    public async Task AcquireAsync_CreatesOwnedNonDeletableSecret_WhenAbsent()
    {
        await using var lease = await _manager.AcquireAsync(Instance("inst-secret"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        var secret = Assert.Single(_k8s.Created<V1Secret>());
        Assert.Equal("inst-secret-proc-deployer", secret.Metadata.Name);
        // Keep the password Secret even when the owner is pruned by a GitOps sync (Argo annotation).
        Assert.Equal("Delete=false", secret.Metadata.Annotations["argocd.argoproj.io/sync-options"]);
        // No password hash in metadata any more: rotation is detected with PWDCOMPARE.
        Assert.DoesNotContain(LegacyHashAnnotation, secret.Metadata.Annotations);
    }

    [Fact]
    public async Task AcquireAsync_RotatesPassword_WhenSqlPasswordDiffers()
    {
        _k8s.Seed(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "inst-rotate-proc-deployer",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string> { [LegacyHashAnnotation] = "0000000000000000" },
            },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("CurrentPwd1!") },
        });
        _users.LoginExists = _ => true;
        _users.DatabaseUserExists = (_, _) => true;
        _users.PasswordMatches = (_, _) => false;

        await using var lease = await _manager.AcquireAsync(Instance("inst-rotate"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        Assert.Equal("CurrentPwd1!", lease.Password);
        Assert.Equal((LoginName, "CurrentPwd1!"), Assert.Single(_users.AlterLoginPasswordCalls));
        Assert.Empty(_users.CreateLoginCalls);
        // The legacy hash annotation is removed.
        Assert.DoesNotContain(LegacyHashAnnotation, _k8s.Updated<V1Secret>().Last().Metadata.Annotations);
    }

    [Fact]
    public async Task AcquireAsync_DoesNotTouchLogin_WhenPasswordMatches()
    {
        _k8s.Seed(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = "inst-stable-proc-deployer", NamespaceProperty = "default" },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("StablePwd1!") },
        });
        _users.LoginExists = _ => true;
        _users.DatabaseUserExists = (_, _) => true;

        await using var lease = await _manager.AcquireAsync(Instance("inst-stable"), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        Assert.Equal("StablePwd1!", lease.Password);
        Assert.Empty(_users.AlterLoginPasswordCalls);
        Assert.Empty(_users.CreateLoginCalls);
        Assert.Empty(_users.CreateDatabaseUserCalls);
    }

    [Fact]
    public async Task AcquireAsync_CreatesLoginOnEveryReplica_WithConsistentSid_ForAvailabilityGroup()
    {
        var sid = new byte[] { 1, 2, 3, 4 };
        _users.LoginSid = sid;
        _k8s.SetGet("ag", "default", new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag", Uid = "uid-ag" } });

        var ag = new ResolvedTarget("SqlAvailabilityGroup", "ag", "default", "ag.default.svc.cluster.local", 1433, "saPwd!", 3);
        await using var lease = await _manager.AcquireAsync(ag, new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        // Each of the 3 replicas re-creates the login with the primary's SID so the database-user
        // mapping survives a failover.
        Assert.Equal(3, _users.CreateLoginCalls.Count(c => c.Login == LoginName && c.Sid is not null && c.Sid.SequenceEqual(sid)));
    }

    [Fact]
    public async Task AcquireAsync_ConvergesPasswordOnReplicas_ForAvailabilityGroup()
    {
        // A rotation used to reach the primary only.
        _users.LoginExists = _ => true;
        _users.DatabaseUserExists = (_, _) => true;
        _users.PasswordMatches = (_, _) => false;
        _k8s.SetGet("ag2", "default", new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag2", Uid = "uid-ag2" } });

        var ag = new ResolvedTarget("SqlAvailabilityGroup", "ag2", "default", "ag2.default.svc.cluster.local", 1433, "saPwd!", 3);
        await using var lease = await _manager.AcquireAsync(ag, new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        Assert.Equal(4, _users.AlterLoginPasswordCalls.Count); // primary + 3 replicas
    }
}
