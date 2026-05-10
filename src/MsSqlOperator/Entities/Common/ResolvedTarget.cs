namespace MsSqlOperator.Entities.Common;

public record ResolvedTarget(
    string Kind,        // "SqlInstance" | "SqlAvailabilityGroup"
    string Name,
    string Namespace,
    string Host,
    int Port,
    string SaPassword,
    int? ReplicaCount);
