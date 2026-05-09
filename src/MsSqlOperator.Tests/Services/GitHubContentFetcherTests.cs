using FluentAssertions;
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
        InvokeGlobMatch("usp_GetCustomers.sql", "*.sql").Should().BeTrue();
        InvokeGlobMatch("usp_GetCustomers.cs", "*.sql").Should().BeFalse();
    }

    [Fact]
    public void GlobMatch_DoubleStarWildcard_MatchesNestedPaths()
    {
        InvokeGlobMatch("src/db/procedures/usp_Test.sql", "**/*.sql").Should().BeTrue();
        InvokeGlobMatch("src/db/procedures/usp_Test.cs", "**/*.sql").Should().BeFalse();
    }

    [Fact]
    public void GlobMatch_ExactMatch_Works()
    {
        InvokeGlobMatch("init.sql", "init.sql").Should().BeTrue();
        InvokeGlobMatch("other.sql", "init.sql").Should().BeFalse();
    }
}
