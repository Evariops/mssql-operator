using FluentAssertions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class DatabaseSelectorTests
{
    private static readonly List<string> AllDatabases =
        ["master", "model", "msdb", "tempdb", "OrdersDB", "UsersDB", "AnalyticsDB", "TestDB"];

    [Fact]
    public void BuildSnapshot_AllUserDatabases_SelectsNonSystem()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeEquivalentTo(
            ["AnalyticsDB", "OrdersDB", "TestDB", "UsersDB"]);
    }

    [Fact]
    public void BuildSnapshot_AllUserDatabases_ExcludeSystemTrue_FiltersSysDbs()
    {
        var selector = new DatabaseSelectorSpec
        {
            AllUserDatabases = true,
            ExcludeSystemDatabases = true
        };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().NotContain("master")
            .And.NotContain("tempdb")
            .And.NotContain("model")
            .And.NotContain("msdb");
    }

    [Fact]
    public void BuildSnapshot_ByNames_SelectsExact()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "UsersDB"] };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeEquivalentTo(["OrdersDB", "UsersDB"]);
    }

    [Fact]
    public void BuildSnapshot_ByNamePatterns_SelectsMatching()
    {
        var selector = new DatabaseSelectorSpec { NamePatterns = ["*DB"] };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeEquivalentTo(
            ["AnalyticsDB", "OrdersDB", "TestDB", "UsersDB"]);
    }

    [Fact]
    public void BuildSnapshot_ExcludeNames_RemovesSpecified()
    {
        var selector = new DatabaseSelectorSpec
        {
            AllUserDatabases = true,
            ExcludeNames = ["TestDB"]
        };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().NotContain("TestDB");
    }

    [Fact]
    public void BuildSnapshot_ExcludePatterns_RemovesMatching()
    {
        var selector = new DatabaseSelectorSpec
        {
            AllUserDatabases = true,
            ExcludePatterns = ["Test*"]
        };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().NotContain("TestDB");
    }

    [Fact]
    public void BuildSnapshot_EmptySelector_ReturnsEmpty()
    {
        var selector = new DatabaseSelectorSpec();
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Should().BeEmpty();
    }

    [Fact]
    public void BuildSnapshot_SetsObservedGeneration()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 42);

        snapshot.ObservedGeneration.Should().Be(42);
    }

    [Fact]
    public void BuildSnapshot_ComputesSelectionHash()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var s1 = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);
        var s2 = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        s1.SelectionHash.Should().Be(s2.SelectionHash);
        s1.SelectionHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BuildSnapshot_DifferentSelection_DifferentHash()
    {
        var s1 = DatabaseSelector.BuildSnapshot(
            new DatabaseSelectorSpec { Names = ["OrdersDB"] }, AllDatabases, 1);
        var s2 = DatabaseSelector.BuildSnapshot(
            new DatabaseSelectorSpec { Names = ["UsersDB"] }, AllDatabases, 1);

        s1.SelectionHash.Should().NotBe(s2.SelectionHash);
    }

    [Fact]
    public void BuildSnapshot_DatabasesSorted()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public void GetMissingExplicitNames_ReturnsNonExistent()
    {
        var selector = new DatabaseSelectorSpec { Names = ["OrdersDB", "MissingDB", "GoneDB"] };
        var missing = DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases);

        missing.Should().BeEquivalentTo(["MissingDB", "GoneDB"]);
    }

    [Fact]
    public void GetMissingExplicitNames_NoNames_ReturnsEmpty()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var missing = DatabaseSelector.GetMissingExplicitNames(selector, AllDatabases);
        missing.Should().BeEmpty();
    }

    [Fact]
    public void GlobMatch_Star_MatchesAll()
    {
        DatabaseSelector.GlobMatch("anything", "*").Should().BeTrue();
    }

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

    [Fact]
    public void BuildSnapshot_CombinedSelector_NamesAndExclude()
    {
        var selector = new DatabaseSelectorSpec
        {
            AllUserDatabases = true,
            ExcludeNames = ["TestDB"],
            ExcludePatterns = ["Analytics*"]
        };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeEquivalentTo(["OrdersDB", "UsersDB"]);
    }

    [Fact]
    public void BuildSnapshot_NamePatternsAndNames_Deduplicates()
    {
        var selector = new DatabaseSelectorSpec
        {
            Names = ["OrdersDB"],
            NamePatterns = ["Orders*"]
        };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Select(d => d.Name).Should().BeEquivalentTo(["OrdersDB"]);
    }

    [Fact]
    public void BuildSnapshot_AllPhasesPending()
    {
        var selector = new DatabaseSelectorSpec { AllUserDatabases = true };
        var snapshot = DatabaseSelector.BuildSnapshot(selector, AllDatabases, 1);

        snapshot.Databases.Should().AllSatisfy(d => d.Phase.Should().Be("Pending"));
    }
}
