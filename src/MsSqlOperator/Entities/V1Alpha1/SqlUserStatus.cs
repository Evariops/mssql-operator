using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlUserStatus
{
    public bool LoginCreated { get; set; }
    public List<DatabaseUserStatus>? DatabaseUsers { get; set; }
    public List<ConditionStatus>? Conditions { get; set; }
}

public class DatabaseUserStatus
{
    public string Database { get; set; } = string.Empty;
    public bool Created { get; set; }
    public List<string>? Roles { get; set; }
}
