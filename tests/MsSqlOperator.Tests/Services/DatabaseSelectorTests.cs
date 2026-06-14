using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class DatabaseSelectorTests
{
    private static readonly List<string> AllDatabases =
        ["master", "model", "msdb", "tempdb", "OrdersDB", "UsersDB", "AnalyticsDB", "TestDB"];

    [Fact]
    public void Select_AllUserDatabases_SelectsNonSystem()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        Assert.Equivalent(new[] { "AnalyticsDB", "OrdersDB", "TestDB", "UsersDB" }, DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_AllUserDatabases_FiltersSysDbs()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludeSystemDatabases = true };
        var dbs = DatabaseSelector.Select(selector, AllDatabases);
        Assert.DoesNotContain("master", dbs);
        Assert.DoesNotContain("tempdb", dbs);
        Assert.DoesNotContain("model", dbs);
        Assert.DoesNotContain("msdb", dbs);
    }

    [Fact]
    public void Select_ByNames_SelectsExact()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "UsersDB"] };
        Assert.Equivalent(new[] { "OrdersDB", "UsersDB" }, DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_ByNamePatterns_SelectsMatching()
    {
        var selector = new DatabaseSelectorSpec { NamePatterns = ["*DB"] };
        Assert.Equivalent(new[] { "AnalyticsDB", "OrdersDB", "TestDB", "UsersDB" }, DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_ExcludeNames_RemovesSpecified()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludeNames = ["TestDB"] };
        Assert.DoesNotContain("TestDB", DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_ExcludePatterns_RemovesMatching()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludePatterns = ["Test*"] };
        Assert.DoesNotContain("TestDB", DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_EmptySelector_ReturnsEmpty()
    {
        Assert.Empty(DatabaseSelector.Select(new DatabaseSelectorSpec(), AllDatabases));
    }

    [Fact]
    public void Select_DatabasesSorted()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var dbs = DatabaseSelector.Select(selector, AllDatabases);
        Assert.Equal(dbs.OrderBy(d => d, StringComparer.Ordinal).ToList(), dbs);
    }

    [Fact]
    public void Select_CombinedSelector_NamesAndExclude()
    {
        var selector = new DatabaseSelectorSpec
        {
            AllUserDatabases = true,
            ExcludeNames = ["TestDB"],
            ExcludePatterns = ["Analytics*"]
        };
        Assert.Equivalent(new[] { "OrdersDB", "UsersDB" }, DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void Select_NamePatternsAndNames_Deduplicates()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB"], NamePatterns = ["Orders*"] };
        Assert.Equivalent(new[] { "OrdersDB" }, DatabaseSelector.Select(selector, AllDatabases));
    }

    [Fact]
    public void GetMissingExplicitNames_ReturnsNonExistent()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "MissingDB", "GoneDB"] };
        Assert.Equivalent(new[] { "MissingDB", "GoneDB" }, DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases));
    }

    [Fact]
    public void GetMissingExplicitNames_NoNames_ReturnsEmpty()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        Assert.Empty(DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases));
    }

    [Fact]
    public void GlobMatch_Star_MatchesAll() =>
        Assert.True(DatabaseSelector.GlobMatch("anything", "*"));

    [Fact]
    public void GlobMatch_Prefix_MatchesSuffix()
    {
        Assert.True(DatabaseSelector.GlobMatch("TestDB", "Test*"));
        Assert.False(DatabaseSelector.GlobMatch("OrdersDB", "Test*"));
    }

    [Fact]
    public void GlobMatch_Suffix_MatchesPrefix()
    {
        Assert.True(DatabaseSelector.GlobMatch("TestDB", "*DB"));
        Assert.False(DatabaseSelector.GlobMatch("TestTable", "*DB"));
    }

    [Fact]
    public void GlobMatch_QuestionMark_MatchesSingleChar()
    {
        Assert.True(DatabaseSelector.GlobMatch("DB1", "DB?"));
        Assert.False(DatabaseSelector.GlobMatch("DB12", "DB?"));
    }

    [Fact]
    public void GlobMatch_CaseInsensitive()
    {
        Assert.True(DatabaseSelector.GlobMatch("OrdersDB", "orders*"));
        Assert.True(DatabaseSelector.GlobMatch("ordersdb", "Orders*"));
    }
}
