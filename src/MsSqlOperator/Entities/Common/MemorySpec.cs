namespace MsSqlOperator.Entities.Common;

/// <summary>Unified memory configuration — single source of truth for pod and SQL Server memory.</summary>
public class MemorySpec
{
    /// <summary>Total memory allocated to the pod (e.g. "16Gi"). Sets both requests and limits for Guaranteed QoS.</summary>
    public string Total { get; set; } = string.Empty;

    /// <summary>Memory reserved for OS, SQL Agent, sidecars. Default "2Gi". SQL max memory = Total - SystemReserve.</summary>
    public string SystemReserve { get; set; } = "2Gi";

    /// <summary>Optional: sp_configure 'min server memory' (e.g. "8Gi"). If omitted, SQL Server uses its default (0).</summary>
    public string? MinMemory { get; set; }
}

/// <summary>Memory alignment status reported on SqlInstance and SqlAvailabilityGroup.</summary>
public class MemoryStatus
{
    /// <summary>Pod resources.limits.memory in MB.</summary>
    public int PodLimitMb { get; set; }

    /// <summary>sp_configure 'max server memory' value applied in MB.</summary>
    public int SqlMaxMemoryMb { get; set; }

    /// <summary>sp_configure 'min server memory' value applied in MB (0 if not set).</summary>
    public int SqlMinMemoryMb { get; set; }

    /// <summary>System reserve in MB.</summary>
    public int SystemReserveMb { get; set; }

    /// <summary>True if the SQL Server configuration matches the calculated value.</summary>
    public bool Aligned { get; set; }
}

/// <summary>Helper to parse Kubernetes memory quantities and compute SQL memory values.</summary>
public static class MemoryCalculator
{
    /// <summary>Parse a Kubernetes memory quantity string to megabytes.</summary>
    public static int ParseToMb(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
            throw new ArgumentException("Memory quantity cannot be empty", nameof(quantity));

        // Trim and parse
        quantity = quantity.Trim();

        if (quantity.EndsWith("Ti", StringComparison.OrdinalIgnoreCase))
            return (int)(double.Parse(quantity[..^2]) * 1024 * 1024);
        if (quantity.EndsWith("Gi", StringComparison.OrdinalIgnoreCase))
            return (int)(double.Parse(quantity[..^2]) * 1024);
        if (quantity.EndsWith("Mi", StringComparison.OrdinalIgnoreCase))
            return (int)double.Parse(quantity[..^2]);
        if (quantity.EndsWith("Ki", StringComparison.OrdinalIgnoreCase))
            return (int)(double.Parse(quantity[..^2]) / 1024);

        // Plain bytes (no suffix)
        if (long.TryParse(quantity, out var bytes))
            return (int)(bytes / (1024 * 1024));

        throw new ArgumentException($"Cannot parse memory quantity: '{quantity}'", nameof(quantity));
    }

    /// <summary>Calculate the SQL max server memory from a MemorySpec.</summary>
    public static int CalculateSqlMaxMemoryMb(MemorySpec spec)
    {
        var totalMb = ParseToMb(spec.Total);
        var reserveMb = ParseToMb(spec.SystemReserve);
        return totalMb - reserveMb;
    }

    /// <summary>Calculate the SQL min server memory from a MemorySpec. Returns 0 if not set.</summary>
    public static int CalculateSqlMinMemoryMb(MemorySpec spec)
    {
        return spec.MinMemory != null ? ParseToMb(spec.MinMemory) : 0;
    }
}
