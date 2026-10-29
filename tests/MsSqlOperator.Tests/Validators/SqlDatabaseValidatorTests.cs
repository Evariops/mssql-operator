using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Tests.Fakes;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlDatabaseValidatorTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly SqlDatabaseValidator _sut;

    public SqlDatabaseValidatorTests()
    {
        _sut = new SqlDatabaseValidator(_k8s, NullLogger<SqlDatabaseValidator>.Instance);
    }

    private static SqlDatabase Db(Action<SqlDatabaseSpec>? configure = null, string uid = "uid-1")
    {
        var entity = new SqlDatabase
        {
            Metadata = new V1ObjectMeta { Name = "db", NamespaceProperty = "sql", Uid = uid },
            Spec = new SqlDatabaseSpec { InstanceRef = new() { Name = "reporting" }, DatabaseName = "Reporting" },
        };
        configure?.Invoke(entity.Spec);
        return entity;
    }

    private static Action<SqlDatabaseSpec> OnAg(Action<SqlDatabaseSpec>? more = null) => s =>
    {
        s.InstanceRef = null;
        s.AvailabilityGroupRef = new() { Name = "orders" };
        more?.Invoke(s);
    };

    [Fact]
    public void Minimal_IsAccepted() => Assert.True(_sut.ValidateSpec(Db()).Valid);

    [Fact]
    public void NeitherOrBothTargets_AreRefused()
    {
        Assert.False(_sut.ValidateSpec(Db(s => s.InstanceRef = null)).Valid);
        Assert.False(_sut.ValidateSpec(Db(s => s.AvailabilityGroupRef = new() { Name = "orders" })).Valid);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("1abc")]
    [InlineData("my-db")]
    [InlineData("")]
    public void InvalidDatabaseNames_AreRefused(string name) => Assert.False(_sut.ValidateSpec(Db(s => s.DatabaseName = name)).Valid);

    [Fact]
    public void AnAgDatabase_IsInFullRecovery()
    {
        Assert.False(_sut.ValidateSpec(Db(OnAg(s => s.RecoveryModel = "Simple"))).Valid);
        Assert.True(_sut.ValidateSpec(Db(OnAg(s => s.RecoveryModel = "Full"))).Valid);
        Assert.True(_sut.ValidateSpec(Db(s => s.RecoveryModel = "Simple")).Valid);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(155)]
    [InlineData(180)]
    public void UnknownCompatibilityLevels_AreRefused(int level) => Assert.False(_sut.ValidateSpec(Db(s => s.CompatibilityLevel = level)).Valid);

    [Theory]
    [InlineData("1Gi", "256Mi", "Unlimited", true)]
    [InlineData("1Gi", "10%", "10Gi", true)]
    [InlineData("big", null, null, false)]
    [InlineData("1Gi", "0%", null, false)]
    [InlineData("2Gi", null, "1Gi", false)]
    public void FileSizes(string size, string? growth, string? max, bool valid)
        => Assert.Equal(valid, _sut.ValidateSpec(Db(s => s.Files = new() { Data = new() { Size = size, Growth = growth, MaxSize = max } })).Valid);

    [Fact]
    public void DeletionPolicy_IsRetainOrDelete() => Assert.False(_sut.ValidateSpec(Db(s => s.DeletionPolicy = "Purge")).Valid);

    [Fact]
    public void AvailabilityGroupSettings_NeedAnAg_AndFollowItsRules()
    {
        Assert.False(_sut.ValidateSpec(Db(s => s.AvailabilityGroup = new())).Valid);
        Assert.False(_sut.ValidateSpec(Db(OnAg(s => s.AvailabilityGroup = new() { LogBackup = new() { ThresholdPercent = 5 } }))).Valid);
        Assert.True(_sut.ValidateSpec(Db(OnAg(s => s.AvailabilityGroup = new() { LogBackup = new() { Mode = "Local" } }))).Valid);
    }

    [Fact]
    public void Update_KeepsTheTargetTheNameAndTheCollation()
    {
        var old = Db(s => s.Collation = "French_CI_AS");
        Assert.False(_sut.Update(old, Db(s => { s.Collation = "French_CI_AS"; s.DatabaseName = "Other"; }), false).Valid);
        Assert.False(_sut.Update(old, Db(s => { s.Collation = "French_CI_AS"; s.InstanceRef = new() { Name = "other" }; }), false).Valid);
        Assert.False(_sut.Update(old, Db(s => s.Collation = "Latin1_General_CI_AS"), false).Valid);
        Assert.True(_sut.Update(old, Db(s => { s.Collation = "French_CI_AS"; s.RecoveryModel = "Simple"; }), false).Valid);
    }

    [Fact]
    public async Task Create_TheSameDatabaseTwice_IsRefused()
    {
        _k8s.SetGet("reporting", "sql", new SqlInstance());
        _k8s.SetList<SqlDatabase>([Db(uid: "uid-other")]);

        Assert.False((await _sut.CreateAsync(Db(), false, CancellationToken.None)).Valid);
    }

    [Fact]
    public async Task Create_ASecondDatabaseInABasicAg_IsRefused()
    {
        _k8s.SetGet("orders", "sql", new SqlAvailabilityGroup { Spec = new SqlAvailabilityGroupSpec { Edition = "Standard" } });
        _k8s.SetList<SqlAvailabilityGroupDatabase>(
        [
            new SqlAvailabilityGroupDatabase
            {
                Spec = new SqlAvailabilityGroupDatabaseSpec { AvailabilityGroupRef = new() { Name = "orders" }, DatabaseName = "First" },
            },
        ]);

        Assert.False((await _sut.CreateAsync(Db(OnAg()), false, CancellationToken.None)).Valid);
    }
}
