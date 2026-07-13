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
    /// <summary>Default Kata containers VM overhead in MB.</summary>
    public const int DefaultKataOverheadMb = 150;

    /// <summary>Parse a Kubernetes memory quantity string to megabytes.</summary>
    public static int ParseToMb(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
            throw new ArgumentException("Memory quantity cannot be empty", nameof(quantity));

        // Parse through the official ResourceQuantity — handles every Kubernetes
        // notation (16G, 1000M, 2e3M, "1.5Gi" under any runtime culture) and rejects values
        // the API server would reject (the old double.Parse accepted "16gi" and broke on
        // fr-FR decimal commas).
        decimal bytes;
        try
        {
            bytes = new k8s.Models.ResourceQuantity(quantity.Trim()).ToDecimal();
        }
        catch (Exception e)
        {
            throw new ArgumentException($"'{quantity}' is not a valid Kubernetes quantity", nameof(quantity), e);
        }

        var mb = bytes / (1024m * 1024m);
        if (mb > int.MaxValue)
            throw new ArgumentException($"'{quantity}' exceeds the supported memory range", nameof(quantity));
        return (int)mb;
    }

    /// <summary>
    /// Fraction of the process memory limit given to <c>max server memory</c>.
    ///
    /// <c>max server memory</c> caps the buffer pool and most caches, but NOT everything sqlservr
    /// allocates: thread stacks (~2 MB × max worker threads), CLR, linked-server providers, backup
    /// buffers, and — the reason this operator exists — the full-text filter daemon (fdhost) and its
    /// crawl buffers, which live entirely outside the budget. Setting max server memory equal to the
    /// process limit therefore leaves ZERO head-room inside the cgroup: the extra allocations push
    /// the container over its limit and the kernel OOM-kills it, most likely during the full-text
    /// population storm at startup — the exact failure this setting prevents.
    /// </summary>
    public const double MaxServerMemoryFraction = 0.9;

    /// <summary>Memory limit of the sqlservr process (mssql.conf <c>memorylimitmb</c> and
    /// <c>MSSQL_MEMORY_LIMIT_MB</c>): everything the pod has, minus what the OS and the agent need.</summary>
    public static int CalculateProcessMemoryLimitMb(MemorySpec spec, string? runtimeClassName = null)
    {
        var totalMb = ParseToMb(spec.Total);
        var reserveMb = ParseToMb(spec.SystemReserve);
        var kataOverheadMb = IsKataRuntime(runtimeClassName) ? DefaultKataOverheadMb : 0;
        return totalMb - reserveMb - kataOverheadMb;
    }

    /// <summary>sp_configure <c>max server memory</c>: a fraction of the process limit, leaving
    /// intra-process head-room.</summary>
    public static int CalculateSqlMaxMemoryMb(MemorySpec spec, string? runtimeClassName = null)
    {
        var processLimitMb = CalculateProcessMemoryLimitMb(spec, runtimeClassName);
        // Never fall below the engine's own minimum (128 MB) on tiny instances.
        return Math.Max(128, (int)(processLimitMb * MaxServerMemoryFraction));
    }

    /// <summary>Calculate the SQL min server memory from a MemorySpec. Returns 0 if not set.</summary>
    public static int CalculateSqlMinMemoryMb(MemorySpec spec)
    {
        return spec.MinMemory != null ? ParseToMb(spec.MinMemory) : 0;
    }

    /// <summary>Check if a runtime class name refers to a Kata containers runtime.</summary>
    public static bool IsKataRuntime(string? runtimeClassName)
    {
        return runtimeClassName != null &&
               runtimeClassName.Contains("kata", StringComparison.OrdinalIgnoreCase);
    }
}
