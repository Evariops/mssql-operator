using MsSqlOperator.Operator;

namespace MsSqlOperator.Tests.Operator;

/// <summary>Failpoints are inert unless armed, and do what they are armed with.</summary>
public class FailpointTests
{
    [Fact]
    public async Task Unarmed_DoesNothing() => await Failpoint.HitAsync("test-unarmed");

    [Fact]
    public async Task ArmedWithThrow_Throws()
    {
        Failpoint.Arm("test-throw", "throw");
        try
        {
            await Assert.ThrowsAsync<FailpointException>(() => Failpoint.HitAsync("test-throw").AsTask());
        }
        finally
        {
            Failpoint.Disarm("test-throw");
        }
    }

    [Fact]
    public async Task ArmedWithCount_FiresOnTheNthHitOnly()
    {
        Failpoint.Arm("test-count", "count:2:throw");
        try
        {
            await Failpoint.HitAsync("test-count");
            await Assert.ThrowsAsync<FailpointException>(() => Failpoint.HitAsync("test-count").AsTask());
            await Failpoint.HitAsync("test-count");
        }
        finally
        {
            Failpoint.Disarm("test-count");
        }
    }

    [Fact]
    public void Parse_ReadsTheEnvironmentSyntax()
        => Assert.Equal([KeyValuePair.Create("after-intent", "throw"), KeyValuePair.Create("api-latency", "delay:2000")],
            Failpoint.Parse("after-intent=throw; api-latency=delay:2000;bogus").ToList());
}
