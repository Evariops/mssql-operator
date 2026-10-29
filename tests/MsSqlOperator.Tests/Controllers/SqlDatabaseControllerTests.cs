using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlDatabaseControllerTests
{
    private const string Ns = "sql";
    private const string InstanceHost = "reporting.sql.svc.cluster.local";
    private const string Primary = "orders-0.orders-headless.sql.svc.cluster.local";

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlDatabaseManager _dbs = new();
    private readonly FakeTargetResolver _resolver = new();
    private readonly FakeEventPublisher _events = new();
    private readonly SqlDatabaseController _sut;

    public SqlDatabaseControllerTests()
    {
        _sut = new SqlDatabaseController(NullLogger<SqlDatabaseController>.Instance, _k8s, new FakeSqlConnectionFactory(), _dbs,
            _resolver, _events, new FakeTimeProvider());
    }

    private static SqlDatabase OnInstance(Action<SqlDatabaseSpec>? configure = null, string uid = "uid-1")
    {
        var entity = new SqlDatabase
        {
            Metadata = new V1ObjectMeta { Name = "reporting-db", NamespaceProperty = Ns, Uid = uid, Generation = 1 },
            Spec = new SqlDatabaseSpec { InstanceRef = new() { Name = "reporting" }, DatabaseName = "Reporting" },
        };
        configure?.Invoke(entity.Spec);
        return entity;
    }

    private static SqlDatabase OnAg(Action<SqlDatabaseSpec>? configure = null)
    {
        var entity = new SqlDatabase
        {
            Metadata = new V1ObjectMeta { Name = "orders-db", NamespaceProperty = Ns, Uid = "uid-ag", Generation = 1 },
            Spec = new SqlDatabaseSpec { AvailabilityGroupRef = new() { Name = "orders" }, DatabaseName = "Orders" },
        };
        configure?.Invoke(entity.Spec);
        return entity;
    }

    private void InstanceRunning(string phase = Phases.Running)
    {
        _resolver.Resolved = [new ResolvedTarget(TargetKinds.Instance, "reporting", Ns, InstanceHost, 1433, "pw", null)];
        _k8s.SetGet("reporting", Ns, new SqlInstance { Status = new SqlInstanceStatus { Phase = phase } });
    }

    private void AgRunning()
    {
        _resolver.Resolved = [new ResolvedTarget(TargetKinds.AvailabilityGroup, "orders", Ns, "orders-listener.sql.svc.cluster.local", 1433, "pw", 3)];
        _resolver.Primary = ("orders-0", new SqlConnection($"Server={Primary}"));
        _k8s.SetGet("orders", Ns, new SqlAvailabilityGroup { Status = new SqlAvailabilityGroupStatus { Phase = Phases.Running, PrimaryReplica = "orders-0" } });
    }

    [Fact]
    public async Task TargetNotRunning_WaitsAndCreatesNothing()
    {
        InstanceRunning(Phases.Pending);

        var result = await _sut.ReconcileAsync(OnInstance(), CancellationToken.None);

        Assert.Equal(Phases.Pending, result.Entity!.Status!.Phase);
        Assert.Empty(_dbs.Calls);
    }

    [Fact]
    public async Task MissingDatabase_IsCreated_WithItsOptions_AndOwned()
    {
        InstanceRunning();
        var entity = OnInstance(s => { s.Collation = "French_CI_AS"; s.RecoveryModel = "Simple"; s.CompatibilityLevel = 150; });

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        var db = _dbs.Find(InstanceHost, "Reporting")!;
        Assert.Equal("French_CI_AS", db.Collation);
        Assert.Equal("SIMPLE", db.RecoveryModel);
        Assert.Equal(150, db.CompatibilityLevel);
        Assert.Equal(SqlDatabaseController.OwnerTag(entity), db.OwnerTag);
        Assert.Equal(Phases.Ready, result.Entity!.Status!.Phase);
        Assert.False(result.Entity.Status.Adopted);
        Assert.Contains("DatabaseCreated", _events.Reasons);
    }

    [Fact]
    public async Task ExistingUnownedDatabase_IsAdopted_NotCreated()
    {
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting");
        var entity = OnInstance();

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.DoesNotContain(_dbs.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal(SqlDatabaseController.OwnerTag(entity), _dbs.Find(InstanceHost, "Reporting")!.OwnerTag);
        Assert.True(result.Entity!.Status!.Adopted);
        Assert.Contains("DatabaseAdopted", _events.Reasons);
    }

    [Fact]
    public async Task DatabaseOwnedByAnotherLiveSqlDatabase_IsLeftAlone()
    {
        InstanceRunning();
        var other = OnInstance(uid: "uid-other");
        other.Metadata.Name = "other";
        _k8s.SetGet("other", Ns, other);
        _dbs.Add(InstanceHost, "Reporting", d => d.OwnerTag = SqlDatabaseController.OwnerTag(other));

        var result = await _sut.ReconcileAsync(OnInstance(s => s.RecoveryModel = "Simple"), CancellationToken.None);

        Assert.Equal(Phases.Conflict, result.Entity!.Status!.Phase);
        Assert.Empty(_dbs.Calls);
        Assert.Equal("FULL", _dbs.Find(InstanceHost, "Reporting")!.RecoveryModel);
    }

    [Fact]
    public async Task DatabaseOfAFormerIncarnation_IsTakenOver()
    {
        // GitOps re-created the resource: same name, new UID, the old one is gone.
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting", d => d.OwnerTag = SqlDatabaseController.OwnerTag(OnInstance(uid: "uid-old")));
        var entity = OnInstance(uid: "uid-new");
        _k8s.SetGet("reporting-db", Ns, entity);

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(Phases.Ready, result.Entity!.Status!.Phase);
        Assert.Equal(SqlDatabaseController.OwnerTag(entity), _dbs.Find(InstanceHost, "Reporting")!.OwnerTag);
        Assert.Contains("DatabaseOwnershipTakenOver", _events.Reasons);
    }

    [Fact]
    public async Task OwnerLogin_AppliedOnceItExists()
    {
        InstanceRunning();
        var entity = OnInstance(s => s.Owner = "reporting_owner");

        var first = await _sut.ReconcileAsync(entity, CancellationToken.None);
        Assert.Contains(first.Entity!.Status!.Conditions!, c => c is { Type: "OwnerLoginMissing", Status: "True" });
        Assert.Equal(TimeSpan.FromSeconds(30), first.RequeueAfter); // Not the 5-minute resync
        Assert.DoesNotContain(_dbs.Calls, c => c.StartsWith("owner", StringComparison.Ordinal));

        _dbs.Logins.Add("reporting_owner");
        var second = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal("reporting_owner", _dbs.Find(InstanceHost, "Reporting")!.Owner);
        Assert.Contains(second.Entity!.Status!.Conditions!, c => c is { Type: "OwnerLoginMissing", Status: "False" });
    }

    [Fact]
    public async Task Files_OnlyGrow()
    {
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting", d => { d.DataSize = 2L << 30; d.LogSize = 1L << 20; });
        var entity = OnInstance(s => s.Files = new SqlDatabaseFilesSpec
        {
            Data = new SqlDatabaseFileSpec { Size = "1Gi" },        // smaller than the 2Gi file: ignored
            Log = new SqlDatabaseFileSpec { Size = "512Mi", Growth = "256Mi" },
        });

        await _sut.ReconcileAsync(entity, CancellationToken.None);

        var db = _dbs.Find(InstanceHost, "Reporting")!;
        Assert.Equal(2L << 30, db.DataSize);
        Assert.Equal(512L << 20, db.LogSize);
        Assert.Equal(256L << 20, db.LogGrowth);
        Assert.DoesNotContain(_dbs.Calls, c => c.Contains("Reporting Reporting size=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DataFileLargerThanTheVolume_IsRefusedBeforeCreation()
    {
        InstanceRunning();
        _k8s.SetGet("data-reporting-0", Ns, new V1PersistentVolumeClaim
        {
            Status = new V1PersistentVolumeClaimStatus { Capacity = new Dictionary<string, k8s.Models.ResourceQuantity> { ["storage"] = new("10Gi") } },
        });

        var result = await _sut.ReconcileAsync(OnInstance(s => s.Files = new() { Data = new() { Size = "20Gi" } }), CancellationToken.None);

        Assert.Equal(Phases.Failed, result.Entity!.Status!.Phase);
        Assert.Contains("does not fit", result.Entity.Status.Conditions!.Single(c => c.Type == "Ready").Message);
        Assert.Empty(_dbs.Calls);
    }

    [Fact]
    public async Task GrowingAFileBeyondItsVolume_IsReported_TheOtherOptionsApplied()
    {
        // MODIFY FILE failed with OS error 31 on every pass, the status still said Ready.
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting");
        _k8s.SetGet("data-reporting-0", Ns, new V1PersistentVolumeClaim
        {
            Status = new V1PersistentVolumeClaimStatus { Capacity = new Dictionary<string, k8s.Models.ResourceQuantity> { ["storage"] = new("4Gi") } },
        });
        var entity = OnInstance(s => { s.CompatibilityLevel = 150; s.Files = new() { Data = new() { Size = "10Gi", Growth = "128Mi" } }; });

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        var db = _dbs.Find(InstanceHost, "Reporting")!;
        Assert.Equal(8L << 20, db.DataSize);
        Assert.Equal(128L << 20, db.DataGrowth);
        Assert.Equal(150, db.CompatibilityLevel);
        var options = result.Entity!.Status!.Conditions!.Single(c => c.Type == "OptionsApplied");
        Assert.Equal("False", options.Status);
        Assert.Contains("does not fit", options.Message);
        Assert.Contains("OptionsNotApplied", _events.Reasons);
        Assert.NotNull(result.RequeueAfter);
    }

    [Fact]
    public async Task GrowingAFileBeyondTheFreeSpace_IsNotAttempted()
    {
        // A failed growth left the file extended to the end of the volume.
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting");
        _dbs.AvailableBytes = 1L << 30;

        var result = await _sut.ReconcileAsync(OnInstance(s => s.Files = new() { Data = new() { Size = "2Gi" } }), CancellationToken.None);

        Assert.DoesNotContain(_dbs.Calls, c => c.StartsWith("file", StringComparison.Ordinal));
        Assert.Contains("MB free on the volume", result.Entity!.Status!.Conditions!.Single(c => c.Type == "OptionsApplied").Message);
    }

    [Fact]
    public async Task AGrowthThatFailsAnyway_GivesItsSpaceBack()
    {
        InstanceRunning();
        _dbs.Add(InstanceHost, "Reporting");
        _dbs.FailNextGrowth = true;

        await _sut.ReconcileAsync(OnInstance(s => s.Files = new() { Data = new() { Size = "1Gi" } }), CancellationToken.None);

        Assert.Contains(_dbs.Calls, c => c == $"release {InstanceHost} Reporting Reporting {8L << 20}");
    }

    [Fact]
    public async Task OnAg_CreatesOnThePrimary_ThenTheAvailabilityGroupDatabase()
    {
        AgRunning();
        var entity = OnAg(s => s.AvailabilityGroup = new SqlDatabaseAvailabilityGroupSpec
        {
            LogBackup = new AgDatabaseLogBackupSpec { Mode = "Local", RetentionHours = 72 },
        });

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(_dbs.Find(Primary, "Orders"));
        var agd = Assert.IsType<SqlAvailabilityGroupDatabase>(Assert.Single(_k8s.CreateCalls));
        Assert.Equal("orders", agd.Spec.AvailabilityGroupRef.Name);
        Assert.Equal("Orders", agd.Spec.DatabaseName);
        Assert.Equal("Local", agd.Spec.LogBackup!.Mode);
        Assert.Equal("uid-ag", agd.Metadata.Annotations![SqlDatabaseController.ManagedByUidAnnotation]);
        Assert.Equal(Phases.Joining, result.Entity!.Status!.Phase);
    }

    [Fact]
    public async Task OnAg_OwnCopyLeftOnTheFormerPrimary_IsBackedUpAndDropped_ThenCreatedOnTheNewPrimary()
    {
        // Created on orders-0, failover to orders-1 before the join.
        AgRunning();
        _resolver.Primary = ("orders-1", new SqlConnection("Server=orders-1.orders-headless.sql.svc.cluster.local"));
        var entity = OnAg();
        _dbs.Add(Primary, "Orders", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Null(_dbs.Find(Primary, "Orders"));
        Assert.Contains(_dbs.Calls, c => c.StartsWith($"orphan {Primary} Orders /var/opt/mssql/backup/mssql-operator/Orders_orphan_", StringComparison.Ordinal));
        Assert.NotNull(_dbs.Find("orders-1.orders-headless.sql.svc.cluster.local", "Orders"));
        Assert.Contains("OrphanCopyDropped", _events.Reasons);
        Assert.Contains(result.Entity!.Status!.Conditions!, c => c is { Type: "OrphanCopy", Status: "True" });
    }

    [Fact]
    public async Task OnAg_OwnCopyWithDataOnTheFormerPrimary_IsLeftAlone_NothingIsCreated()
    {
        // An adopted database waited for its backup window outside the AG; after a failover
        // it was backed up, dropped, and replaced by an empty one on the new primary.
        AgRunning();
        _resolver.Primary = ("orders-1", new SqlConnection("Server=orders-1.orders-headless.sql.svc.cluster.local"));
        var entity = OnAg();
        _dbs.Add(Primary, "Orders", d => { d.OwnerTag = SqlDatabaseController.OwnerTag(entity); d.HasUserObjects = true; });

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.NotNull(_dbs.Find(Primary, "Orders"));
        Assert.Null(_dbs.Find("orders-1.orders-headless.sql.svc.cluster.local", "Orders"));
        Assert.DoesNotContain(_dbs.Calls, c => c.StartsWith("orphan", StringComparison.Ordinal) || c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal(Phases.Failed, result.Entity!.Status!.Phase);
        Assert.Contains(result.Entity.Status.Conditions!, c => c is { Type: "Ready", Reason: "DatabaseOnAFormerPrimary" });
    }

    [Fact]
    public async Task OnAg_ForeignCopyOnAReplica_RefusesTheCreation()
    {
        AgRunning();
        _dbs.Add("orders-2.orders-headless.sql.svc.cluster.local", "Orders");

        var result = await _sut.ReconcileAsync(OnAg(), CancellationToken.None);

        Assert.Equal(Phases.Failed, result.Entity!.Status!.Phase);
        Assert.DoesNotContain(_dbs.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnAg_ReadyOnceTheAvailabilityGroupDatabaseIsSynchronized()
    {
        AgRunning();
        var entity = OnAg();
        _dbs.Add(Primary, "Orders", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _k8s.SetList<SqlAvailabilityGroupDatabase>([ManagedAgd(entity, Phases.Synchronized)]);

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(Phases.Ready, result.Entity!.Status!.Phase);
        Assert.Empty(_k8s.CreateCalls);
        Assert.Empty(_k8s.PatchCalls);
    }

    [Fact]
    public async Task OnAg_LogBackupModeNotSet_IsReportedOnTheSqlDatabase_ThenCleared()
    {
        AgRunning();
        var entity = OnAg();
        _dbs.Add(Primary, "Orders", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        var agd = ManagedAgd(entity, Phases.Synchronized);
        agd.Status!.Conditions = [new ConditionStatus { Type = "LogBackupModeDeprecated", Status = "True", Reason = "ModeNotSet", Message = "Set spec.availabilityGroup.logBackup.mode" }];
        _k8s.SetList<SqlAvailabilityGroupDatabase>([agd]);

        var first = await _sut.ReconcileAsync(entity, CancellationToken.None);
        Assert.Contains(first.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupModeDeprecated", Status: "True", Reason: "ModeNotSet" }
                                                                && c.Message!.Contains("spec.availabilityGroup.logBackup.mode"));

        agd.Status.Conditions = [];
        var second = await _sut.ReconcileAsync(first.Entity!, CancellationToken.None);
        Assert.Contains(second.Entity!.Status!.Conditions!, c => c is { Type: "LogBackupModeDeprecated", Status: "False" });
    }

    [Fact]
    public async Task OnAg_ExistingAvailabilityGroupDatabase_IsAdopted_ItsSettingsKept()
    {
        AgRunning();
        var entity = OnAg();
        _dbs.Add(Primary, "Orders");
        var existing = new SqlAvailabilityGroupDatabase
        {
            Metadata = new V1ObjectMeta { Name = "orders-legacy", NamespaceProperty = Ns },
            Spec = new SqlAvailabilityGroupDatabaseSpec
            {
                AvailabilityGroupRef = new() { Name = "orders" },
                DatabaseName = "Orders",
                LogBackup = new AgDatabaseLogBackupSpec { Mode = "Disabled" },
            },
        };
        _k8s.SetList<SqlAvailabilityGroupDatabase>([existing]);

        await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Empty(_k8s.CreateCalls);
        var patch = Assert.Single(_k8s.PatchMetadataCalls);
        Assert.Equal("uid-ag", patch.Annotations[SqlDatabaseController.ManagedByUidAnnotation]);
        Assert.Empty(_k8s.PatchCalls); // the SqlDatabase sets no availabilityGroup block: Disabled stays
    }

    [Fact]
    public async Task OnAg_AvailabilityGroupDatabaseManagedByAnotherLiveSqlDatabase_IsAConflict()
    {
        AgRunning();
        var other = OnAg();
        other.Metadata.Name = "orders-db-2";
        other.Metadata.Uid = "uid-other";
        _k8s.SetGet("orders-db-2", Ns, other);
        var entity = OnAg();
        _dbs.Add(Primary, "Orders", d => d.OwnerTag = SqlDatabaseController.OwnerTag(entity));
        _k8s.SetList<SqlAvailabilityGroupDatabase>([ManagedAgd(other, Phases.Synchronized)]);

        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);

        Assert.Equal(Phases.Conflict, result.Entity!.Status!.Phase);
        Assert.Empty(_k8s.PatchMetadataCalls);
    }

    private static SqlAvailabilityGroupDatabase ManagedAgd(SqlDatabase owner, string phase) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = owner.Metadata.Name,
            NamespaceProperty = Ns,
            Annotations = new Dictionary<string, string>
            {
                [SqlDatabaseController.ManagedByAnnotation] = owner.Metadata.Name,
                [SqlDatabaseController.ManagedByUidAnnotation] = owner.Metadata.Uid!,
            },
        },
        Spec = new SqlAvailabilityGroupDatabaseSpec { AvailabilityGroupRef = new() { Name = "orders" }, DatabaseName = "Orders" },
        Status = new SqlAvailabilityGroupDatabaseStatus { Phase = phase },
    };
}
