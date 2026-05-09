namespace MsSqlOperator.Entities.Common;

public class AntiAffinitySpec
{
    public List<AntiAffinityRule>? RequiredAntiAffinityWith { get; set; }
    public List<PreferredAntiAffinityRule>? PreferredAntiAffinityWith { get; set; }
    public SelfAntiAffinityRule? RequiredSelfAntiAffinity { get; set; }
    public PreferredSelfAntiAffinityRule? PreferredSelfAntiAffinity { get; set; }
}

public class AntiAffinityRule
{
    public string InstanceName { get; set; } = string.Empty;
    public string TopologyKey { get; set; } = "kubernetes.io/hostname";
}

public class PreferredAntiAffinityRule : AntiAffinityRule
{
    public int Weight { get; set; } = 100;
}

public class SelfAntiAffinityRule
{
    public string TopologyKey { get; set; } = "kubernetes.io/hostname";
}

public class PreferredSelfAntiAffinityRule : SelfAntiAffinityRule
{
    public int Weight { get; set; } = 100;
}
