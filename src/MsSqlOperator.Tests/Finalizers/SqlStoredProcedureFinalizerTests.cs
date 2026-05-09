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

public class SqlStoredProcedureFinalizerTests
{
    private readonly Mock<ILogger<SqlStoredProcedureFinalizer>> _logger = new();
    private readonly Mock<IKubernetesClient> _k8sClient = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlStoredProcedureManager> _procManager = new();
    private readonly SqlStoredProcedureFinalizer _finalizer;

    public SqlStoredProcedureFinalizerTests()
    {
        _finalizer = new SqlStoredProcedureFinalizer(_logger.Object, _k8sClient.Object, _connFactory.Object, _procManager.Object);
    }

    [Fact]
    public async Task FinalizeAsync_DropsDeployedProcedures()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test-procs", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec { InstanceRef = new ResourceReference { Name = "my-sql" } },
            Status = new SqlStoredProcedureStatus
            {
                DeployedProcedures =
                [
                    new DeployedDatabaseProcedures
                    {
                        Database = "AppDb",
                        Procedures = [new DeployedProcedureInfo { Name = "usp_Get", Schema = "dbo" }]
                    }
                ]
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

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _procManager.Verify(x => x.DropProcedureAsync(connection, "AppDb", "dbo", "usp_Get", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalizeAsync_NoDeployedProcedures_ReturnsSuccess()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec(),
            Status = null
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);
        result.Entity.Should().NotBeNull();
    }
}
