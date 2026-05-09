using FluentAssertions;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlUserFinalizerTests
{
    private readonly Mock<ILogger<SqlUserFinalizer>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlUserManager> _userManager = new();
    private readonly Mock<ISqlServerManager> _serverManager = new();
    private readonly SqlUserFinalizer _finalizer;

    public SqlUserFinalizerTests()
    {
        _finalizer = new SqlUserFinalizer(_logger.Object, _k8sClient.Object, _connFactory.Object, _userManager.Object, _serverManager.Object);
    }

    [Fact]
    public async Task FinalizeAsync_DropsUsersAndLogin()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test-user", NamespaceProperty = "default" },
            Spec = new SqlUserSpec
            {
                InstanceRef = new ResourceReference { Name = "my-sql" },
                Login = new LoginSpec { Name = "app_user" }
            }
        };

        var instance = new SqlInstance
        {
            Spec = new SqlInstanceSpec { SaPasswordSecretRef = new SecretReference { Name = "sa-secret", Key = "password" } }
        };
        var secret = new V1Secret { Data = new Dictionary<string, byte[]> { { "password", "P@ss!"u8.ToArray() } } };
        var connection = new Microsoft.Data.SqlClient.SqlConnection();

        _k8sClient.Setup(x => x.GetAsync<SqlInstance>("my-sql", "default", It.IsAny<CancellationToken>())).ReturnsAsync(instance);
        _k8sClient.Setup(x => x.GetAsync<V1Secret>("sa-secret", "default", It.IsAny<CancellationToken>())).ReturnsAsync(secret);
        _connFactory.Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        _serverManager.Setup(x => x.GetDatabaseNamesAsync(connection, It.IsAny<CancellationToken>())).ReturnsAsync(["AppDb", "LogDb"]);
        _userManager.Setup(x => x.DatabaseUserExistsAsync(connection, It.IsAny<string>(), "app_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _userManager.Setup(x => x.LoginExistsAsync(connection, "app_user", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
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
            Spec = new SqlUserSpec { InstanceRef = new ResourceReference { Name = "my-sql" } }
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        result.Entity.Should().NotBeNull();
    }

    [Fact]
    public async Task FinalizeAsync_NoInstanceRef_ReturnsSuccess()
    {
        var entity = new SqlUser
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlUserSpec { Login = new LoginSpec { Name = "user1" } }
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        result.Entity.Should().NotBeNull();
    }
}
