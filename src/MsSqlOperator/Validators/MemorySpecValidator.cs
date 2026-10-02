using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

public static class MemorySpecValidator
{
    public static (bool Valid, string? Message) Validate(MemorySpec memory)
    {
        int totalMb, reserveMb;
        try { totalMb = MemoryCalculator.ParseToMb(memory.Total); }
        catch (ArgumentException) { return (false, $"Invalid memory.total value: '{memory.Total}'"); }

        try { reserveMb = MemoryCalculator.ParseToMb(memory.SystemReserve); }
        catch (ArgumentException) { return (false, $"Invalid memory.systemReserve value: '{memory.SystemReserve}'"); }

        if (totalMb < 4096)
            return (false, $"memory.total must be at least 4Gi (4096 MB). Got: {totalMb} MB");

        if (reserveMb < 512)
            return (false, $"memory.systemReserve must be at least 512Mi (512 MB). Got: {reserveMb} MB");

        var sqlMaxMb = totalMb - reserveMb;
        if (sqlMaxMb < 2048)
            return (false, $"SQL Server memory (total - systemReserve) must be at least 2Gi (2048 MB). Got: {sqlMaxMb} MB");

        if (memory.MinMemory != null)
        {
            int minMb;
            try { minMb = MemoryCalculator.ParseToMb(memory.MinMemory); }
            catch (ArgumentException) { return (false, $"Invalid memory.minMemory value: '{memory.MinMemory}'"); }

            if (minMb >= sqlMaxMb)
                return (false, $"memory.minMemory ({minMb} MB) must be less than SQL max memory ({sqlMaxMb} MB)");
        }

        return (true, null);
    }
}
