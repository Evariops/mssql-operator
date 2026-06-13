using FluentAssertions;
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
        DatabaseSelector.Select(selector, AllDatabases)
            .Should().BeEquivalentTo(["AnalyticsDB", "OrdersDB", "TestDB", "UsersDB"]);
    }

    [Fact]
    public void Select_AllUserDatabases_FiltersSysDbs()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludeSystemDatabases = true };
        DatabaseSelector.Select(selector, AllDatabases)
            .Should().NotContain("master").And.NotContain("tempdb").And.NotContain("model").And.NotContain("msdb");
    }

    [Fact]
    public void Select_ByNames_SelectsExact()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "UsersDB"] };
        DatabaseSelector.Select(selector, AllDatabases).Should().BeEquivalentTo(["OrdersDB", "UsersDB"]);
    }

    [Fact]
    public void Select_ByNamePatterns_SelectsMatching()
    {
        var selector = new DatabaseSelectorSpec { NamePatterns = ["*DB"] };
        DatabaseSelector.Select(selector, AllDatabases)
            .Should().BeEquivalentTo(["AnalyticsDB", "OrdersDB", "TestDB", "UsersDB"]);
    }

    [Fact]
    public void Select_ExcludeNames_RemovesSpecified()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludeNames = ["TestDB"] };
        DatabaseSelector.Select(selector, AllDatabases).Should().NotContain("TestDB");
    }

    [Fact]
    public void Select_ExcludePatterns_RemovesMatching()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true, ExcludePatterns = ["Test*"] };
        DatabaseSelector.Select(selector, AllDatabases).Should().NotContain("TestDB");
    }

    [Fact]
    public void Select_EmptySelector_ReturnsEmpty()
    {
        DatabaseSelector.Select(new DatabaseSelectorSpec(), AllDatabases).Should().BeEmpty();
    }

    [Fact]
    public void Select_DatabasesSorted()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        DatabaseSelector.Select(selector, AllDatabases).Should().BeInAscendingOrder();
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
        DatabaseSelector.Select(selector, AllDatabases).Should().BeEquivalentTo(["OrdersDB", "UsersDB"]);
    }

    [Fact]
    public void Select_NamePatternsAndNames_Deduplicates()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB"], NamePatterns = ["Orders*"] };
        DatabaseSelector.Select(selector, AllDatabases).Should().BeEquivalentTo(["OrdersDB"]);
    }

    [Fact]
    public void GetMissingExplicitNames_ReturnsNonExistent()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "MissingDB", "GoneDB"] };
        DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases)
            .Should().BeEquivalentTo(["MissingDB", "GoneDB"]);
    }

    [Fact]
    public void GetMissingExplicitNames_NoNames_ReturnsEmpty()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases).Should().BeEmpty();
    }

    [Fact]
    public void GlobMatch_Star_MatchesAll() =>
        DatabaseSelector.GlobMatch("anything", "*").Should().BeTrue();

    [Fact]
    public void GlobMatch_Prefix_MatchesSuffix()
    {
        DatabaseSelector.GlobMatch("TestDB", "Test*").Should().BeTrue();
        DatabaseSelector.GlobMatch("OrdersDB", "Test*").Should().BeFalse();
    }

    [Fact]
    public void GlobMatch_Suffix_MatchesPrefix()
    {
        DatabaseSelector.GlobMatch("TestDB", "*DB").Should().BeTrue();
        DatabaseSelector.GlobMatch("TestTable", "*DB").Should().BeFalse();
    }

    [Fact]
    public void GlobMatch_QuestionMark_MatchesSingleChar()
    {
        DatabaseSelector.GlobMatch("DB1", "DB?").Should().BeTrue();
        DatabaseSelector.GlobMatch("DB12", "DB?").Should().BeFalse();
    }

    [Fact]
    public void GlobMatch_CaseInsensitive()
    {
        DatabaseSelector.GlobMatch("OrdersDB", "orders*").Should().BeTrue();
        DatabaseSelector.GlobMatch("ordersdb", "Orders*").Should().BeTrue();
    }
}
