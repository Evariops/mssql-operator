using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Control;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Services;

/// <summary>The durability profile.</summary>
public class DurabilityProfileTests
{
    private static SqlAvailabilityGroup Ag(int count, string edition = "Enterprise", string? durability = null, string? phase = null, int floor = 0) => new()
    {
        Metadata = new V1ObjectMeta { Name = "sales", NamespaceProperty = "db" },
        Spec = new SqlAvailabilityGroupSpec
        {
            Edition = edition,
            Replicas = new AgReplicaTopologySpec { Count = count, SynchronousCount = count, Durability = durability, RequiredSyncFloor = floor },
        },
        Status = phase is null ? null! : new SqlAvailabilityGroupStatus { Phase = phase },
    };

    [Theory]
    [InlineData(3, "Enterprise", null, null, "Quorum")]   // a new Enterprise AG of 3
    [InlineData(2, "Enterprise", null, null, "Legacy")]   // 2 replicas: no quorum of secondaries
    [InlineData(3, "Standard", null, null, "Legacy")]
    [InlineData(3, "Enterprise", null, "Running", "Legacy")] // existed before the profiles
    [InlineData(3, "Enterprise", "Legacy", null, "Legacy")]  // explicit
    public void Effective_FollowsTheDefaults(int count, string edition, string? durability, string? phase, string expected)
        => Assert.Equal(expected, DurabilityProfile.Effective(Ag(count, edition, durability, phase), null));

    [Fact]
    public void Effective_TheProfileFixedInTheRegistryWins_OverTheDefault()
    {
        var record = new SqlControlRecord { Spec = new SqlControlRecordSpec { Durability = new ControlDurability { Profile = "Quorum" } } };
        Assert.Equal("Quorum", DurabilityProfile.Effective(Ag(3, phase: "Running"), record));
    }

    [Theory]
    [InlineData("Quorum", 0, 1)]
    [InlineData("Legacy", 0, 0)]
    [InlineData("Quorum", 2, 2)]
    public void Floor_IsAtLeastOneInQuorum(string profile, int configured, int expected)
        => Assert.Equal(expected, DurabilityProfile.Floor(Ag(3, floor: configured), profile));
}
