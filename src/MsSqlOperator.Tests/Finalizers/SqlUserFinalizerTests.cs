using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlUserFinalizerTests
{
    private readonly Mock<ILogger<SqlUserFinalizer>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlUserManager> _userManager = new();
    private readonly Mock<ISqlServerManager> _serverManager = new();
    private readonly Mock<ITargetResolver> _targetResolver = new();
    private readonly SqlUserFinalizer _finalizer;

    public SqlUserFinalizerTests()
    {
        _finalizer = new SqlUserFinalizer(_logger.Object, _k8sClient.Object, _connFactory.Object, _userManager.Object, _serverManager.Object, _targetResolver.Object);
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

        var resolved = new List<ResolvedTarget>
        {
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        };
        _targetResolver.Setup(x => x.ResolveAsync(entity.Spec.Target, "default", It.IsAny<CancellationToken>())).ReturnsAsync(resolved);

        var connection = new Microsoft.Data.SqlClient.SqlConnection();
        _connFactory.Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        _serverManager.Setup(x => x.GetDatabaseNamesAsync(connection, It.IsAny<CancellationToken>())).ReturnsAsync(["AppDb", "LogDb"]);
        _userManager.Setup(x => x.DatabaseUserExistsAsync(connection, It.IsAny<string>(), "app_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _userManager.Setup(x => x.LoginExistsAsync(connection, "app_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _userManager.Verify(x => x.KillSessionsAsync(connection, "app_user", It.IsAny<CancellationToken>()), Times.Once);
        _userManager.Verify(x => x.DropDatabaseUserAsync(connection, "AppDb", "app_user", It.IsAny<CancellationToken>()), Times.Once);
        _userManager.Verify(x => x.DropDatabaseUserAsync(connection, "LogDb", "app_user", It.IsAny<CancellationToken>()), Times.Once);
        _userManager.Verify(x => x.DropLoginAsync(connection, "app_user", It.IsAny<CancellationToken>()), Times.Once);
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
        result.Entity.Should().NotBeNull();
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

        _targetResolver.Setup(x => x.ResolveAsync(entity.Spec.Target, "default", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        result.Entity.Should().NotBeNull();
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

        var resolved = new List<ResolvedTarget>
        {
            new("SqlInstance", "db1", "default", "db1.default.svc.cluster.local", 1433, "pass", null),
            new("SqlInstance", "db2", "default", "db2.default.svc.cluster.local", 1433, "pass", null)
        };
        _targetResolver.Setup(x => x.ResolveAsync(entity.Spec.Target, "default", It.IsAny<CancellationToken>())).ReturnsAsync(resolved);

        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        _connFactory.Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(conn);
        _serverManager.Setup(x => x.GetDatabaseNamesAsync(conn, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _userManager.Setup(x => x.LoginExistsAsync(conn, "multi_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _userManager.Verify(x => x.KillSessionsAsync(conn, "multi_user", It.IsAny<CancellationToken>()), Times.Exactly(2));
        _userManager.Verify(x => x.DropLoginAsync(conn, "multi_user", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
