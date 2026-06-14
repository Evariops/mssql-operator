using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class SqlIdentifierRulesTests
{
    [Theory]
    [InlineData("app_user")]
    [InlineData("AppUser42")]
    [InlineData("_internal")]
    [InlineData("my-login")]
    [InlineData("clientèle")] // unicode letters allowed
    public void Check_AcceptsReasonableIdentifiers(string value)
        => Assert.Null(SqlIdentifierRules.Check(value, "f"));

    [Theory]
    [InlineData("a]b")]       // bracket escape attempt
    [InlineData("a'b")]       // quote
    [InlineData("a b")]       // space (excluded)
    [InlineData("a.b")]       // dot (excluded)
    [InlineData("1abc")]      // leading digit
    [InlineData("a\nb")]      // newline
    [InlineData("a;DROP")]    // statement separator
    public void Check_RejectsExoticIdentifiers(string value)
        => Assert.NotNull(SqlIdentifierRules.Check(value, "f"));

    [Fact]
    public void Check_Rejects129Characters()
        => Assert.Contains("128", SqlIdentifierRules.Check(new string('a', 129), "f")!);

    [Fact]
    public void Check_Accepts128Characters()
        => Assert.Null(SqlIdentifierRules.Check(new string('a', 128), "f"));

    [Fact]
    public void Check_RejectsEmpty()
        => Assert.Contains("required", SqlIdentifierRules.Check("", "f")!);

    [Fact]
    public void CheckOptional_AcceptsAbsent()
        => Assert.Null(SqlIdentifierRules.CheckOptional(null, "f"));

    [Theory]
    [InlineData("sysadmin")]
    [InlineData("SECURITYADMIN")]
    [InlineData("serveradmin")]
    public void PrivilegedServerRoles_MatchCaseInsensitive(string role)
        => Assert.Contains(role, SqlIdentifierRules.PrivilegedServerRoles);

    [Fact]
    public void AllowsPrivilegedRoles_RequiresExplicitTrue()
    {
        Assert.False(SqlIdentifierRules.AllowsPrivilegedRoles(null));
        Assert.False(SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>()));
        Assert.False(SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>
        { [SqlIdentifierRules.AllowPrivilegedRolesAnnotation] = "false" }));
        Assert.True(SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>
        { [SqlIdentifierRules.AllowPrivilegedRolesAnnotation] = "true" }));
    }
}
