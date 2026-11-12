using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Finalizers;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Finalizers;

public class SqlDatabaseFinalizerTests
{
    private const string Ns = "sql";
    private const string InstanceHost = "reporting.sql.svc.cluster.local";

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlDatabaseManager _dbs = new();
    private readonly FakeTargetResolver _resolver = new();
    private readonly FakeEventPublisher _events = new();
    private readonly SqlDatabaseFinalizer _sut;

    public SqlDatabaseFinalizerTests()
    {
        _sut = new SqlDatabaseFinalizer(NullLogger<SqlDatabaseFinalizer>.Instance, _k8s, new FakeSqlConnectionFactory(), _dbs,
            _resolver, _events);
    }

    private static string Replica(int i) => $"orders-{i}.orders-headless.sql.svc.cluster.local";

    private static SqlDatabase Entity(string policy, bool onAg = false, string? mode = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "the-db", NamespaceProperty = Ns, Uid = "uid-1" },
        Spec = new SqlDatabaseSpec
        {
            InstanceRef = onAg ? null : new() { Name = "reporting" },
            AvailabilityGroupRef = onAg ? new() { Name = "orders" } : null,
            DatabaseName = "Data",
            DeletionPolicy = policy,
            Automation = mode is null ? null : new AutomationSpec { Mode = mode },
        },
    };

    private void Instance() => _resolver.Resolved = [new ResolvedTarget(TargetKinds.Instance, "reporting", Ns, InstanceHost, 1433, "pw", null)];

    private void Ag()
    {
        _resolver.Resolved = [new ResolvedTarget(TargetKinds.AvailabilityGroup, "orders", Ns, "", 1433, "pw", 3)];
        _resolver.Primary = ("orders-0", new SqlConnection($"Server={Replica(0)}"));
    }

    private SqlAvailabilityGroupDatabase Managed(SqlDatabase owner) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "the-db",
            NamespaceProperty = Ns,
            Annotations = new Dictionary<string, string>
            {
                [SqlDatabaseController.ManagedByAnnotation] = owner.Metadata.Name,
                [SqlDatabaseController.ManagedByUidAnnotation] = owner.Metadata.Uid!,
            },
        },
        Spec = new SqlAvailabilityGroupDatabaseSpec { AvailabilityGroupRef = new() { Name = "orders" }, DatabaseName = "Data" },
    };

    [Fact]
    public async Task Retain_DropsNothing_AndReleasesTheAvailabilityGroupDatabase()
    {
        Ag();
        var entity = Entity("Retain", onAg: true);
        _dbs.Add(Replica(0), "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _k8s.SetList<SqlAvailabilityGroupDatabase>([Managed(entity)]);

        var result = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(_dbs.Calls);
        Assert.Empty(_k8s.DeleteCalls);
        var release = Assert.Single(_k8s.PatchMetadataCalls);
        Assert.Null(release.Annotations[SqlDatabaseController.ManagedByUidAnnotation]);
        Assert.Contains("DatabaseRetained", _events.Reasons);
    }

    [Fact]
    public async Task Delete_OnAnInstance_DropsTheDatabaseItOwns()
    {
        Instance();
        var entity = Entity("Delete");
        _dbs.Add(InstanceHost, "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));

        var result = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(_dbs.Find(InstanceHost, "Data"));
        Assert.Contains("DatabaseDropped", _events.Reasons);
    }

    [Fact]
    public async Task Delete_NeverDropsADatabaseOwnedBySomeoneElse()
    {
        Instance();
        _dbs.Add(InstanceHost, "Data", d => d.OwnerTag = "SqlDatabase/sql/other/uid-other");

        var result = await _sut.FinalizeAsync(Entity("Delete"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(_dbs.Find(InstanceHost, "Data"));
        Assert.Contains("DatabaseNotDropped", _events.Reasons);
    }

    [Fact]
    public async Task Delete_OnAnAg_FirstTakesTheDatabaseOutOfTheAg()
    {
        Ag();
        var entity = Entity("Delete", onAg: true);
        _dbs.Add(Replica(0), "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _k8s.SetList<SqlAvailabilityGroupDatabase>([Managed(entity)]);

        var result = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.False(result.IsSuccess); // retried until the SqlAvailabilityGroupDatabase is gone
        Assert.Single(_k8s.DeleteCalls);
        Assert.Empty(_dbs.Calls);
        // A SqlAvailabilityGroupDatabase keeps its database in the AG by default: the managed one is
        // told to remove it, before its deletion.
        Assert.Contains(_k8s.PatchCalls, c => c.Operations.Any(o => (string?)o["path"] == "/spec/deletionPolicy"
                                                                    && (string?)o["value"] == SqlAvailabilityGroupDatabaseSpec.RemovePolicy));
    }

    [Fact]
    public async Task Delete_OnAnAg_DropsOnThePrimaryAndTheCopiesOfTheSecondaries()
    {
        Ag();
        var entity = Entity("Delete", onAg: true);
        _dbs.Add(Replica(0), "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _dbs.Add(Replica(1), "Data", d => d.State = "RESTORING");
        _dbs.Add(Replica(2), "Data", d => d.State = "RESTORING");

        var result = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(_dbs.Databases);
    }

    [Fact]
    public async Task Delete_OnAnAg_ACopyStillOnline_IsWaitedFor_NotLeftBehind()
    {
        // a test cluster: seconds after the database left the AG, the copy of a readable secondary was
        // still ONLINE; dropping it failed, the failure was taken for an unreachable replica, and the
        // copies stayed for good.
        Ag();
        var entity = Entity("Delete", onAg: true);
        _dbs.Add(Replica(0), "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _dbs.Add(Replica(1), "Data", d => d.State = "ONLINE");

        var first = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.NotNull(_dbs.Find(Replica(1), "Data"));
        Assert.DoesNotContain("SqlArtifactsOrphaned", _events.Reasons);

        _dbs.Find(Replica(1), "Data")!.State = "RESTORING";
        var second = await _sut.FinalizeAsync(entity, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Empty(_dbs.Databases);
    }

    [Theory]
    [InlineData("Plan")]
    [InlineData("Observe")]
    public async Task Delete_NotInApplyMode_IsWithheld(string mode)
    {
        Instance();
        var entity = Entity("Delete", mode: mode);
        _dbs.Add(InstanceHost, "Data", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));

        await Assert.ThrowsAsync<ActionWithheldException>(() => _sut.FinalizeAsync(entity, CancellationToken.None));

        Assert.NotNull(_dbs.Find(InstanceHost, "Data"));
    }
}
