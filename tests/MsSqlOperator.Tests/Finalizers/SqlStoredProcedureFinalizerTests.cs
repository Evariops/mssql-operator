using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlStoredProcedureFinalizerTests
{
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlStoredProcedureManager _procManager = new();
    private readonly FakeSqlServerManager _sqlServerManager = new();
    private readonly FakeProcDeployerLoginManager _deployerManager = new();
    private readonly FakeTargetResolver _targetResolver = new();
    private readonly SqlStoredProcedureFinalizer _finalizer;

    public SqlStoredProcedureFinalizerTests()
    {
        // The slim status no longer records deployments; the finalizer recomputes the procedures to
        // drop from spec.Inline and resolves databases via the real DatabaseFilter.
        _finalizer = new SqlStoredProcedureFinalizer(
            NullLogger<SqlStoredProcedureFinalizer>.Instance, _connFactory, _procManager,
            _sqlServerManager, new DatabaseFilter(), _deployerManager, _targetResolver);
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

        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _sqlServerManager.DatabaseNames = ["AppDb", "OtherDb"];

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.NotNull(result.Entity);
        Assert.Single(_procManager.DropCalls, c => c.Db == "AppDb" && c.Schema == "dbo" && c.Name == "usp_Get");
        Assert.DoesNotContain(_procManager.DropCalls, c => c.Db == "OtherDb");
        // The drop runs through the least-privilege deployer login, not sa.
        Assert.Single(_deployerManager.EnsureCalls, c => c.Databases.Contains("AppDb"));
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

        Assert.NotNull(result.Entity);
        Assert.Empty(_procManager.DropCalls);
    }
}
