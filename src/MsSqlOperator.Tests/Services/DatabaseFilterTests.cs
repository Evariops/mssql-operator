using FluentAssertions;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.Filters;

namespace MsSqlOperator.Tests.Services;

public class DatabaseFilterTests
{
    private readonly IDatabaseFilter _filter = new DatabaseFilter();

    [Fact]
    public void Apply_WithExplicitDatabases_ReturnsOnlyThoseDatabases()
    {
        var allDbs = new List<string> { "AppDb", "LogDb", "MetricsDb" };
        var result = _filter.Apply(allDbs, ["AppDb", "LogDb"], null);

        result.Should().BeEquivalentTo(["AppDb", "LogDb"]);
    }

    [Fact]
    public void Apply_WithNullDatabases_ReturnsAll()
    {
        var allDbs = new List<string> { "AppDb", "LogDb" };
        var result = _filter.Apply(allDbs, null, null);

        result.Should().BeEquivalentTo(allDbs);
    }

    [Fact]
    public void Apply_WithRegexPattern_FiltersMatching()
    {
        var allDbs = new List<string> { "App_Prod", "App_Staging", "Logs_Prod", "System" };
        var filter = new DatabaseFilterSpec { Pattern = "^App_.*" };

        var result = _filter.Apply(allDbs, null, filter);

        result.Should().BeEquivalentTo(["App_Prod", "App_Staging"]);
    }

    [Fact]
    public void Apply_WithExcludeList_ExcludesMatching()
    {
        var allDbs = new List<string> { "AppDb", "LogDb", "TempDb_Custom" };
        var filter = new DatabaseFilterSpec { Exclude = ["TempDb_Custom"] };

        var result = _filter.Apply(allDbs, null, filter);

        result.Should().BeEquivalentTo(["AppDb", "LogDb"]);
    }

    [Fact]
    public void Apply_WithIncludeAndExclude_AppliesBoth()
    {
        var allDbs = new List<string> { "App_Prod", "App_Test", "Log_Prod", "System" };
        var filter = new DatabaseFilterSpec
        {
            Pattern = "^App_.*",
            Exclude = ["App_Test"]
        };

        var result = _filter.Apply(allDbs, null, filter);

        result.Should().BeEquivalentTo(["App_Prod"]);
    }

    [Fact]
    public void Apply_WithEmptyDatabaseList_ReturnsEmpty()
    {
        var result = _filter.Apply([], ["AppDb"], null);

        result.Should().BeEmpty();
    }
}
