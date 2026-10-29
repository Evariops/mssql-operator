using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Actuation;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// A resource declaring spec.automation must be read by the gateway: one it does not know runs under
/// the operator's ceiling (Apply) whatever its mode. SqlDatabase was missing — its DROP DATABASE ran
/// in Plan mode in the unit tests.
/// </summary>
public class EveryAutomationSpecIsReadByTheGatewayTests
{
    public static TheoryData<string> Kinds()
    {
        var data = new TheoryData<string>();
        foreach (var type in KindsWithAutomation())
            data.Add(type.Name);
        return data;
    }

    private static IEnumerable<Type> KindsWithAutomation()
        => typeof(AutomationSpec).Assembly.GetTypes()
            .Where(t => t.Namespace == "MsSqlOperator.Entities.V1Alpha1" && t.GetProperty("Spec")?.PropertyType.GetProperty("Automation") is not null);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheModeOfTheResource_IsTheOneTheGatewayApplies(string kind)
    {
        var type = KindsWithAutomation().Single(t => t.Name == kind);
        var entity = Activator.CreateInstance(type)!;
        var spec = Activator.CreateInstance(type.GetProperty("Spec")!.PropertyType)!;
        spec.GetType().GetProperty("Automation")!.SetValue(spec, new AutomationSpec { Mode = "Plan" });
        type.GetProperty("Spec")!.SetValue(entity, spec);

        Assert.Equal("Plan", ActuationGateway.AutomationOf(entity)?.Mode);
    }
}
