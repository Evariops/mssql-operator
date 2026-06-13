using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>The least-privilege stored-procedure deployer login lifecycle.</summary>
public class ProcDeployerLoginManagerTests
{
    private const string LoginName = "mssqlop_proc_deployer";
    private const string HashAnnotation = "mssql-operator.evariops.com/proc-deployer-hash";

    private readonly Mock<ILogger<ProcDeployerLoginManager>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8s = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlUserManager> _users = new();
    private readonly ProcDeployerLoginManager _manager;

    // Stand-in for the operator-owned password Secret; mutated by the captured CreateAsync/UpdateAsync.
    private V1Secret? _storedSecret;

    public ProcDeployerLoginManagerTests()
    {
        _manager = new ProcDeployerLoginManager(_logger.Object, _k8s.Object, _connFactory.Object, _users.Object);

        _k8s.Setup(x => x.GetAsync<V1Secret>("inst-proc-deployer", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _storedSecret);
        _k8s.Setup(x => x.CreateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, CancellationToken>((s, _) => _storedSecret = s)
            .ReturnsAsync((V1Secret s, CancellationToken _) => s);
        _k8s.Setup(x => x.UpdateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, CancellationToken>((s, _) => _storedSecret = s)
            .ReturnsAsync((V1Secret s, CancellationToken _) => s);
        _k8s.Setup(x => x.GetAsync<SqlInstance>("inst", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlInstance { Metadata = new V1ObjectMeta { Name = "inst", Uid = "uid-1" } });
    }

    private static ResolvedTarget Instance() =>
        new("SqlInstance", "inst", "default", "inst.default.svc.cluster.local", 1433, "saPwd!", null);

    private static string Sha16(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    [Fact]
    public async Task EnsureAsync_CreatesLoginUserAndRole_WhenAbsent()
    {
        _users.Setup(x => x.LoginExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _users.Setup(x => x.DatabaseUserExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _users.Setup(x => x.GetDatabaseRoleMembershipsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sa = new Microsoft.Data.SqlClient.SqlConnection();
        var password = await _manager.EnsureAsync(Instance(), sa, ["AppDb"], CancellationToken.None);

        password.Should().NotBeNullOrEmpty();
        _users.Verify(x => x.CreateLoginAsync(sa, LoginName, password, null, true, false, null, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(x => x.CreateDatabaseUserAsync(sa, "AppDb", LoginName, LoginName, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(x => x.AddRoleMemberAsync(sa, "AppDb", LoginName, "db_ddladmin", It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(x => x.AlterLoginPasswordAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_CreatesOwnedNonDeletableSecret_WhenAbsent()
    {
        _users.Setup(x => x.LoginExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _users.Setup(x => x.DatabaseUserExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _users.Setup(x => x.GetDatabaseRoleMembershipsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _manager.EnsureAsync(Instance(), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        _storedSecret.Should().NotBeNull();
        _storedSecret!.Metadata.Name.Should().Be("inst-proc-deployer");
        _storedSecret.Metadata.OwnerReferences.Should().ContainSingle(o => o.Uid == "uid-1");
        // Keep the password Secret even when the owner is pruned by a GitOps sync (Argo annotation).
        _storedSecret.Metadata.Annotations["argocd.argoproj.io/sync-options"].Should().Be("Delete=false");
        // The applied-password hash is persisted so a later reconcile can detect Secret rotation.
        _storedSecret.Metadata.Annotations.Should().ContainKey(HashAnnotation);
    }

    [Fact]
    public async Task EnsureAsync_RotatesPassword_WhenSecretHashStale()
    {
        // Existing Secret whose recorded hash does not match its current password → rotation due.
        _storedSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "inst-proc-deployer",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string> { [HashAnnotation] = "0000000000000000" },
            },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("CurrentPwd1!") },
        };
        _users.Setup(x => x.LoginExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _users.Setup(x => x.DatabaseUserExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _users.Setup(x => x.GetDatabaseRoleMembershipsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(["db_ddladmin"]);

        var sa = new Microsoft.Data.SqlClient.SqlConnection();
        var password = await _manager.EnsureAsync(Instance(), sa, ["AppDb"], CancellationToken.None);

        password.Should().Be("CurrentPwd1!");
        _users.Verify(x => x.AlterLoginPasswordAsync(sa, LoginName, "CurrentPwd1!", It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(x => x.CreateLoginAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        // The newly applied password's hash is written back so the next reconcile is a no-op.
        _storedSecret!.Metadata.Annotations[HashAnnotation].Should().Be(Sha16("CurrentPwd1!"));
    }

    [Fact]
    public async Task EnsureAsync_Idempotent_WhenHashMatchesAndEverythingExists()
    {
        // Existing Secret whose hash matches its password, login + user + role already present.
        _storedSecret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = "inst-proc-deployer",
                NamespaceProperty = "default",
                Annotations = new Dictionary<string, string> { [HashAnnotation] = Sha16("StablePwd1!") },
            },
            Data = new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("StablePwd1!") },
        };
        _users.Setup(x => x.LoginExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _users.Setup(x => x.DatabaseUserExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _users.Setup(x => x.GetDatabaseRoleMembershipsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(["db_ddladmin"]);

        var password = await _manager.EnsureAsync(Instance(), new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        password.Should().Be("StablePwd1!");
        _users.Verify(x => x.AlterLoginPasswordAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(x => x.CreateLoginAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(x => x.CreateDatabaseUserAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(x => x.AddRoleMemberAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_CreatesLoginOnEveryReplica_WithConsistentSid_ForAvailabilityGroup()
    {
        var sid = new byte[] { 1, 2, 3, 4 };
        _users.Setup(x => x.LoginExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _users.Setup(x => x.GetLoginSidAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(sid);
        _users.Setup(x => x.DatabaseUserExistsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _users.Setup(x => x.GetDatabaseRoleMembershipsAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), "AppDb", LoginName, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // Per-replica sa connections used to create the SID-consistent login on secondaries.
        _connFactory.Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(() => new Microsoft.Data.SqlClient.SqlConnection());
        _k8s.Setup(x => x.GetAsync<V1Secret>("ag-proc-deployer", "default", It.IsAny<CancellationToken>())).ReturnsAsync(() => _storedSecret);
        _k8s.Setup(x => x.GetAsync<SqlAvailabilityGroup>("ag", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "ag", Uid = "uid-ag" } });

        var ag = new ResolvedTarget("SqlAvailabilityGroup", "ag", "default", "ag.default.svc.cluster.local", 1433, "saPwd!", 3);
        await _manager.EnsureAsync(ag, new Microsoft.Data.SqlClient.SqlConnection(), ["AppDb"], CancellationToken.None);

        // Primary creates the login (sid: null), then each of the 3 replicas re-creates it with the
        // captured SID so the database-user mapping survives a failover.
        _users.Verify(x => x.CreateLoginAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), LoginName, It.IsAny<string>(), null, true, false, sid, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
