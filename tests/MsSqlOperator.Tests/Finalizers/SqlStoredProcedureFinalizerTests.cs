using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlStoredProcedureFinalizerTests
{
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly FakeSqlStoredProcedureManager _procManager = new();
    private readonly FakeSqlServerManager _sqlServerManager = new();
    private readonly FakeTargetResolver _targetResolver = new();
    private readonly SqlStoredProcedureFinalizer _finalizer;

    public SqlStoredProcedureFinalizerTests()
    {
        _finalizer = new SqlStoredProcedureFinalizer(
            NullLogger<SqlStoredProcedureFinalizer>.Instance, _connFactory, _procManager,
            _sqlServerManager, _targetResolver);
    }

    private static string Marker(string owner) => ProcedureMarker.Prefix(owner, "sha256:x") + "ABCDEF";

    [Fact]
    public async Task FinalizeAsync_DropsOnlyProceduresThisResourceDeployed()
    {
        // The marker decides — a same-named procedure deployed by someone else, or never
        // deployed by the operator, is left alone, in any database.
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test-procs", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec
            {
                Target = new TargetSelector { Instances = ["my-sql"] },
                Databases = ["AppDb"],
            }
        };

        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _sqlServerManager.DatabaseNames = ["AppDb", "OtherDb", "MovedDb"];
        _procManager.States["AppDb"] =
        [
            new("dbo", "usp_Get", Marker("default/test-procs"), "ABCDEF"),
            new("dbo", "usp_Dba", null, "123456"),
        ];
        _procManager.States["OtherDb"] = [new("dbo", "usp_Get", Marker("default/another"), "ABCDEF")];
        _procManager.States["MovedDb"] = [new("app", "usp_Old", Marker("default/test-procs"), "ABCDEF")];

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([("AppDb", "dbo", "usp_Get"), ("MovedDb", "app", "usp_Old")], _procManager.DropCalls);
    }

    [Fact]
    public async Task FinalizeAsync_NothingMarked_DropsNothing()
    {
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta { Name = "test", NamespaceProperty = "default" },
            Spec = new SqlStoredProcedureSpec { Target = new TargetSelector { Instances = ["my-sql"] } },
        };
        _targetResolver.Resolved =
        [
            new("SqlInstance", "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)
        ];
        _sqlServerManager.DatabaseNames = ["AppDb"];
        _procManager.States["AppDb"] = [new("dbo", "usp_Get", null, "ABCDEF")];

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(_procManager.DropCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task FinalizeAsync_AgWithoutPrimary_IsRetried_UnlessForceFinalize(bool force, bool released)
    {
        // The AG without a primary was skipped and the finalizer released, the procedures
        // still deployed. It is retried now; force-finalize is the explicit way out.
        var entity = new SqlStoredProcedure
        {
            Metadata = new V1ObjectMeta
            {
                Name = "test-procs",
                NamespaceProperty = "default",
                Annotations = force ? new Dictionary<string, string> { ["mssql-operator.evariops.com/force-finalize"] = "true" } : null,
            },
            Spec = new SqlStoredProcedureSpec { Target = new TargetSelector { AvailabilityGroups = ["my-ag"] } }
        };
        _targetResolver.Resolved = [new(TargetKinds.AvailabilityGroup, "my-ag", "default", "my-ag.default.svc.cluster.local", 1433, "P@ss!", 3)];
        _targetResolver.Primary = null;

        var result = await _finalizer.FinalizeAsync(entity, CancellationToken.None);

        Assert.Equal(released, result.IsSuccess);
    }
}
