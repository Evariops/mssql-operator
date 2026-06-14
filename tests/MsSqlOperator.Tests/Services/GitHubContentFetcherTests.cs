using MsSqlOperator.Services.GitHub;
using System.Reflection;

namespace MsSqlOperator.Tests.Services;

public class GitHubContentFetcherTests
{
    private static bool InvokeGlobMatch(string path, string pattern)
    {
        var method = typeof(GitHubContentFetcher).GetMethod("GlobMatch", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, [path, pattern])!;
    }

    [Fact]
    public void GlobMatch_SimpleWildcard_MatchesSqlFiles()
    {
        Assert.True(InvokeGlobMatch("usp_GetCustomers.sql", "*.sql"));
        Assert.False(InvokeGlobMatch("usp_GetCustomers.cs", "*.sql"));
    }

    [Fact]
    public void GlobMatch_DoubleStarWildcard_MatchesNestedPaths()
    {
        Assert.True(InvokeGlobMatch("src/db/procedures/usp_Test.sql", "**/*.sql"));
        Assert.False(InvokeGlobMatch("src/db/procedures/usp_Test.cs", "**/*.sql"));
    }

    [Fact]
    public void GlobMatch_ExactMatch_Works()
    {
        Assert.True(InvokeGlobMatch("init.sql", "init.sql"));
        Assert.False(InvokeGlobMatch("other.sql", "init.sql"));
    }
}
