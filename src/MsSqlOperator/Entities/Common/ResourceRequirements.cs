namespace MsSqlOperator.Entities.Common;

public class ResourceRequirements
{
    public ResourceQuantity? Requests { get; set; }
    public ResourceQuantity? Limits { get; set; }
    public bool CpuPinning { get; set; }
}

public class ResourceQuantity
{
    public string? Cpu { get; set; }
    public string? Memory { get; set; }
}
