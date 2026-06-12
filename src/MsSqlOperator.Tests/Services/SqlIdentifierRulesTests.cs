using FluentAssertions;
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
        => SqlIdentifierRules.Check(value, "f").Should().BeNull();

    [Theory]
    [InlineData("a]b")]       // bracket escape attempt
    [InlineData("a'b")]       // quote
    [InlineData("a b")]       // space (excluded)
    [InlineData("a.b")]       // dot (excluded)
    [InlineData("1abc")]      // leading digit
    [InlineData("a\nb")]      // newline
    [InlineData("a;DROP")]    // statement separator
    public void Check_RejectsExoticIdentifiers(string value)
        => SqlIdentifierRules.Check(value, "f").Should().NotBeNull();

    [Fact]
    public void Check_Rejects129Characters()
        => SqlIdentifierRules.Check(new string('a', 129), "f").Should().Contain("128");

    [Fact]
    public void Check_Accepts128Characters()
        => SqlIdentifierRules.Check(new string('a', 128), "f").Should().BeNull();

    [Fact]
    public void Check_RejectsEmpty()
        => SqlIdentifierRules.Check("", "f").Should().Contain("required");

    [Fact]
    public void CheckOptional_AcceptsAbsent()
        => SqlIdentifierRules.CheckOptional(null, "f").Should().BeNull();

    [Theory]
    [InlineData("sysadmin")]
    [InlineData("SECURITYADMIN")]
    [InlineData("serveradmin")]
    public void PrivilegedServerRoles_MatchCaseInsensitive(string role)
        => SqlIdentifierRules.PrivilegedServerRoles.Contains(role).Should().BeTrue();

    [Fact]
    public void AllowsPrivilegedRoles_RequiresExplicitTrue()
    {
        SqlIdentifierRules.AllowsPrivilegedRoles(null).Should().BeFalse();
        SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>()).Should().BeFalse();
        SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>
        { [SqlIdentifierRules.AllowPrivilegedRolesAnnotation] = "false" }).Should().BeFalse();
        SqlIdentifierRules.AllowsPrivilegedRoles(new Dictionary<string, string>
        { [SqlIdentifierRules.AllowPrivilegedRolesAnnotation] = "true" }).Should().BeTrue();
    }
}
