using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Entities.V1Alpha1;

public class SqlStoredProcedureSpec
{
    public TargetSelector Target { get; set; } = new();
    public List<string>? Databases { get; set; }
    public DatabaseFilterSpec? DatabaseFilter { get; set; }
    public List<InlineProcedureSpec>? Inline { get; set; }
    public GitHubSourceSpec? GitHub { get; set; }
    public DeploymentOptionsSpec? DeploymentOptions { get; set; }
    public SyncSpec? Sync { get; set; }
}

public class InlineProcedureSpec
{
    public string Name { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
    public string Definition { get; set; } = string.Empty;
}

public class DeploymentOptionsSpec
{
    public string Strategy { get; set; } = "CreateOrAlter";
    public bool Transactional { get; set; } = true;
    public string ExecutionOrder { get; set; } = "Alphabetical";
    public bool ValidateSyntax { get; set; } = true;
    public bool DryRun { get; set; }
}

public class SyncSpec
{
    public int IntervalMinutes { get; set; } = 30;
    public bool FollowLatestRelease { get; set; }
    public string? ReleaseFilter { get; set; }
}
