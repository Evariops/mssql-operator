namespace MsSqlOperator.Entities.Common;

public class GitHubSourceSpec
{
    public string Repository { get; set; } = string.Empty;
    public SecretReference? AuthSecretRef { get; set; }
    public GitHubRefSpec? Ref { get; set; }
    public List<GitHubPathSpec>? Paths { get; set; }
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
}

public class GitHubRefSpec
{
    public string? Tag { get; set; }
    public string? Sha { get; set; }
}

public class GitHubPathSpec
{
    public string Path { get; set; } = string.Empty;
    public string Schema { get; set; } = "dbo";
}
