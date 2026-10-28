using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>The divergence markers the databases carry.</summary>
public class DivergenceMarkersTests
{
    private static DivergenceMarker Marker(int i) => new($"dv-{i}", i, "a", "b", DateTime.UnixEpoch.AddDays(i), 100 + i);

    [Fact]
    public void Serialize_RoundTrips()
    {
        var markers = new[] { Marker(1), Marker(2) };
        Assert.Equal(markers, DivergenceMarkers.Parse(DivergenceMarkers.Serialize(markers)));
    }

    [Fact]
    public void With_IsBounded_AndKeepsTheNewest()
    {
        var markers = Enumerable.Range(0, 20).Aggregate((IReadOnlyList<DivergenceMarker>)[], (all, i) => DivergenceMarkers.With(all, Marker(i)));
        Assert.Equal(DivergenceMarkers.Max, markers.Count);
        Assert.Equal("dv-4", markers[0].Id);
        Assert.True(DivergenceMarkers.Serialize(markers).Length < 3750); // fits an nvarchar sql_variant
    }

    [Fact]
    public void Without_DropsTheSettledOnes()
        => Assert.Equal(["dv-2"], DivergenceMarkers.Without([Marker(1), Marker(2)], ["dv-1"]).Select(m => m.Id));

    [Fact]
    public void Parse_IgnoresGarbage() => Assert.Empty(DivergenceMarkers.Parse("not json"));
}
