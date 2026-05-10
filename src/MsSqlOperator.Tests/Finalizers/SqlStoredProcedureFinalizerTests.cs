using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlStoredProcedureFinalizerTests
{
    private readonly Mock<ILogger<SqlStoredProcedureFinalizer>> _logger = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlStoredProcedureManager> _procManager = new();
    private readonly Mock<ITargetResolver> _targetResolver = new();
    private readonly SqlStoredProcedureFinalizer _finalizer;

    public SqlStoredProcedureFinalizerTests()
    {
        _finalizer = new SqlStoredProcedureFinalizer(_logger.Object, _connFactory.Object, _procManager.Object, _targetResolver.Object);
    }

    [Fact]
    public async Task FinalizeAsync_DropsDeployedProcedures()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test-procs", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec { Target = new TargetSelector { Instances = ["my-sql"] } },
            Status = new SqlStoredProcedureStatus
            {
                TargetStatuses =
                [
                    new StoredProcedureTargetStatus
                    {
                        Kind = "SqlInstance", Name = "my-sql", Namespace = "default",
                        DeployedProcedures =
                        [
                            new DeployedDatabaseProcedures
                            {
                                Database = "AppDb",
                                Procedures = [new DeployedProcedureInfo { Name = "usp_Get", Schema = "dbo" }]
                            }
                        ]
                    }
                ]
            }
        };

        var resolved = new List<ResolvedTarget>
        {
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        };
        _targetResolver.Setup(x => x.ResolveAsync(entity.Spec.Target, "default", It.IsAny<CancellationToken>())).ReturnsAsync(resolved);

        var connection = new Microsoft.Data.SqlClient.SqlConnection();
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
