using System.Security.Cryptography;
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
    private const string HashAnnotation = "mssql-operator.evariops.com/proc-deployer-hash";

    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlUserManager _users = new();
    private readonly ProcDeployerLoginManager _manager;

    public ProcDeployerLoginManagerTests()
    {
        _manager = new ProcDeployerLoginManager(NullLogger<ProcDeployerLoginManager>.Instance, _k8s, _connFactory, _users);
        _k8s.SetGet("inst", "default", new SqlInstance { Metadata = new V1ObjectMeta { Name = "inst", Uid = "uid-1" } });
    }

    private static ResolvedTarget Instance() =>
        new("SqlInstance", "inst", "default", "inst.default.svc.cluster.local", 1433, "saPwd!", null);

    private static string Sha16(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    [Fact]
    public async Task EnsureAsync_CreatesLoginUserAndRole_WhenAbsent()
    {
        var sa = new Microsoft.Data.SqlClient.SqlConnection();
        var password = await _manager.EnsureAsync(Instance(), sa, ["AppDb"], CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(password));
        var login = Assert.Single(_users.CreateLoginCalls);
        Assert.Equal(LoginName, login.Login);
        Assert.Equal(password, login.Pwd);
        Assert.Null(login.DefaultDb);
        Assert.True(login.CheckPolicy);
        Assert.False(login.CheckExpiration);
        Assert.Null(login.Sid);
        Assert.Equal(("AppDb", LoginName, LoginName), Assert.Single(_users.CreateDatabaseUserCalls));
        Assert.Equal(("AppDb", LoginName, "db_ddladmin"), Assert.Single(_users.AddRoleMemberCalls));
        Assert.Empty(_users.AlterLoginPasswordCalls);
    }

    [Fact]
    public async Task EnsureAsync_CreatesOwnedNonDeletableSecret_WhenAbsent()
    {
        await _manager.EnsureAsync(Instance(), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        var secret = Assert.Single(_k8s.Created<V1Secret>());
        Assert.Equal("inst-proc-deployer", secret.Metadata.Name);
        Assert.Single(secret.Metadata.OwnerReferences, o => o.Uid == "uid-1");
        // Keep the password Secret even when the owner is pruned by a GitOps sync (Argo annotation).
        Assert.Equal("Delete=false", secret.Metadata.Annotations["argocd.argoproj.io/sync-options"]);
        // The applied-password hash is persisted so a later reconcile can detect Secret rotation.
        Assert.Contains(HashAnnotation, secret.Metadata.Annotations);
    }

    [Fact]
    public async Task EnsureAsync_RotatesPassword_WhenSecretHashStale()
    {
        // Existing Secret whose recorded hash does not match its current password → rotation due.
        _k8s.Seed(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "inst-proc-deployer",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string> { [HashAnnotation] = "0000000000000000" },
            },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("CurrentPwd1!") },
        });
        _users.LoginExists = _ => true;
        _users.DatabaseUserExists = (_, _) => true;
        _users.DatabaseRoleMemberships = (_, _) => ["db_ddladmin"];

        var sa = new Microsoft.Data.SqlClient.SqlConnection();
        var password = await _manager.EnsureAsync(Instance(), sa, ["AppDb"], CancellationToken.None);

        Assert.Equal("CurrentPwd1!", password);
        Assert.Equal((LoginName, "CurrentPwd1!"), Assert.Single(_users.AlterLoginPasswordCalls));
        Assert.Empty(_users.CreateLoginCalls);
        // The newly applied password's hash is written back so the next reconcile is a no-op.
        var updated = _k8s.Updated<V1Secret>().Last();
        Assert.Equal(Sha16("CurrentPwd1!"), updated.Metadata.Annotations[HashAnnotation]);
    }

    [Fact]
    public async Task EnsureAsync_Idempotent_WhenHashMatchesAndEverythingExists()
    {
        // Existing Secret whose hash matches its password, login + user + role already present.
        _k8s.Seed(new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "inst-proc-deployer",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string> { [HashAnnotation] = Sha16("StablePwd1!") },
            },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("StablePwd1!") },
        });
        _users.LoginExists = _ => true;
        _users.DatabaseUserExists = (_, _) => true;
        _users.DatabaseRoleMemberships = (_, _) => ["db_ddladmin"];

        var password = await _manager.EnsureAsync(Instance(), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        Assert.Equal("StablePwd1!", password);
        Assert.Empty(_users.AlterLoginPasswordCalls);
        Assert.Empty(_users.CreateLoginCalls);
        Assert.Empty(_users.CreateDatabaseUserCalls);
        Assert.Empty(_users.AddRoleMemberCalls);
    }

    [Fact]
    public async Task EnsureAsync_CreatesLoginOnEveryReplica_WithConsistentSid_ForAvailabilityGroup()
    {
        var sid = new byte[] { 1, 2, 3, 4 };
        _users.LoginSid = sid;
        _k8s.SetGet("ag", "default", new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag", Uid = "uid-ag" } });

        var ag = new ResolvedTarget("SqlAvailabilityGroup", "ag", "default", "ag.default.svc.cluster.local", 1433, "saPwd!", 3);
        await _manager.EnsureAsync(ag, new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        // Each of the 3 replicas re-creates the login with the captured SID so the database-user
        // mapping survives a failover.
        Assert.Equal(3, _users.CreateLoginCalls.Count(c => c.Login == LoginName && c.Sid is not null && c.Sid.SequenceEqual(sid)));
    }
}
