using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlUserStatus
{
    public string? EffectiveExpiresAt { get; set; }
    public bool Expired { get; set; }
    public DateTime? RevokedAt { get; set; }
    public List<UserTargetStatus>? TargetStatuses { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class UserTargetStatus
{
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool LoginCreated { get; set; }
    public bool LoginDisabled { get; set; }
    public bool RevocationPending { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int SessionsKilled { get; set; }
    public string? LastRevocationError { get; set; }
    public List<ReplicaLoginStatus>? ReplicaStatuses { get; set; }
    public List<DatabaseUserStatus>? DatabaseUsers { get; set; }
}

public class ReplicaLoginStatus
{
    public string Replica { get; set; } = string.Empty;
    public bool LoginCreated { get; set; }
    public bool LoginDisabled { get; set; }
    public string? Sid { get; set; }
}

public class DatabaseUserStatus
{
    public string Database { get; set; } = string.Empty;
    public bool Created { get; set; }
    public List<string>? Roles { get; set; }
}
