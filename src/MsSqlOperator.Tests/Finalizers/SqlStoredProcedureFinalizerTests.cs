using FluentAssertions;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Moq;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlStoredProcedureFinalizerTests
{
    private readonly Mock<ILogger<SqlStoredProcedureFinalizer>> _logger = new();
    private readonly Mock<ISqlConnectionFactory> _connFactory = new();
    private readonly Mock<ISqlStoredProcedureManager> _procManager = new();
    private readonly Mock<ISqlServerManager> _sqlServerManager = new();
    private readonly Mock<ITargetResolver> _targetResolver = new();
    private readonly SqlStoredProcedureFinalizer _finalizer;

    public SqlStoredProcedureFinalizerTests()
    {
        // The slim status no longer records deployments; the finalizer recomputes the procedures to
        // drop from spec.Inline and resolves databases via the real DatabaseFilter.
        _finalizer = new SqlStoredProcedureFinalizer(
            _logger.Object, _connFactory.Object, _procManager.Object,
            _sqlServerManager.Object, new DatabaseFilter(), _targetResolver.Object);
    }

    [Fact]
    public async Task FinalizeAsync_DropsSpecProceduresFromResolvedDatabases()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test-procs", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] },
                Databases = ["AppDb"],
                Inline = [new InlineProcedureSpec { Name = "usp_Get", Schema = "dbo", Definition = "CREATE PROCEDURE dbo.usp_Get AS SELECT 1" }]
            }
        };

        var resolved = new List<ResolvedTarget>
        {
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        };
        _targetResolver.Setup(x => x.ResolveAsync(entity.Spec.Target, "default", It.IsAny<CancellationToken>())).ReturnsAsync(resolved);

        var connection = new Microsoft.Data.SqlClient.SqlConnection();
        _connFactory.Setup(x => x.CreateConnectionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        _sqlServerManager.Setup(x => x.GetDatabaseNamesAsync(connection, It.IsAny<CancellationToken>())).ReturnsAsync(["AppDb", "OtherDb"]);

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _procManager.Verify(x => x.DropProcedureAsync(connection, "AppDb", "dbo", "usp_Get", It.IsAny<CancellationToken>()), Times.Once);
        _procManager.Verify(x => x.DropProcedureAsync(connection, "OtherDb", "dbo", "usp_Get", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FinalizeAsync_NoInlineProcedures_ReturnsSuccessWithoutDropping()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec { Target = new TargetSelector { Instances = ["my-sql"] } },
        };

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        result.Entity.Should().NotBeNull();
        _procManager.Verify(x => x.DropProcedureAsync(It.IsAny<Microsoft.Data.SqlClient.SqlConnection>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
