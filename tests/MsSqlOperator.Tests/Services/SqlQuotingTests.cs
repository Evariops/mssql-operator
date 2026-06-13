using FluentAssertions;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class SqlQuotingTests
{
    // ── Identifier quoting ─────────────────────────────────

    [Fact]
    public void QuoteIdentifier_WrapsInBrackets()
    {
        SqlQuoting.QuoteIdentifier("my_login").Should().Be("[my_login]");
    }

    [Fact]
    public void QuoteIdentifier_EscapesClosingBracket()
    {
        SqlQuoting.QuoteIdentifier("my]name").Should().Be("[my]]name]");
        SqlQuoting.QuoteIdentifier("a]b]c").Should().Be("[a]]b]]c]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void QuoteIdentifier_RejectsNullOrWhitespace(string? value)
    {
        var act = () => SqlQuoting.QuoteIdentifier(value!);
        act.Should().Throw<ArgumentException>();
    }

    // ── String literal quoting ─────────────────────────────

    [Fact]
    public void QuoteStringLiteral_EscapesSingleQuote()
    {
        SqlQuoting.QuoteStringLiteral("it's").Should().Be("'it''s'");
        SqlQuoting.QuoteStringLiteral("a'b'c").Should().Be("'a''b''c'");
    }

    [Fact]
    public void QuoteNStringLiteral_PrefixesNAndEscapes()
    {
        SqlQuoting.QuoteNStringLiteral("café").Should().Be("N'café'");
        SqlQuoting.QuoteNStringLiteral("d'oh").Should().Be("N'd''oh'");
    }

    // ── Permission allowlists ──────────────────────────────

    [Theory]
    [InlineData("VIEW SERVER STATE")]
    [InlineData("CONTROL SERVER")]
    [InlineData("connect sql")] // case-insensitive
    public void ValidateServerPermission_AllowsKnownPermissions(string permission)
    {
        SqlQuoting.ValidateServerPermission(permission).Should().Be(permission);
    }

    [Theory]
    [InlineData("DROP DATABASE")]
    [InlineData("SELECT")] // schema-level, not a server permission
    [InlineData("VIEW SERVER STATE; DROP TABLE x")] // injection attempt
    public void ValidateServerPermission_RejectsUnknown(string permission)
    {
        var act = () => SqlQuoting.ValidateServerPermission(permission);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("execute")]
    public void ValidateSchemaPermission_AllowsKnownPermissions(string permission)
    {
        SqlQuoting.ValidateSchemaPermission(permission).Should().Be(permission);
    }

    [Theory]
    [InlineData("CONTROL SERVER")] // server-level, not schema
    [InlineData("SELECT--")]
    public void ValidateSchemaPermission_RejectsUnknown(string permission)
    {
        var act = () => SqlQuoting.ValidateSchemaPermission(permission);
        act.Should().Throw<ArgumentException>();
    }

    // ── TDE algorithm allowlist ────────────────────────────

    [Theory]
    [InlineData("AES_256")]
    [InlineData("aes_128")]
    public void ValidateTdeAlgorithm_AllowsKnown(string algo)
    {
        SqlQuoting.ValidateTdeAlgorithm(algo).Should().Be(algo);
    }

    [Theory]
    [InlineData("AES_512")]
    [InlineData("AES_256; DROP DATABASE x")]
    public void ValidateTdeAlgorithm_RejectsUnknown(string algo)
    {
        var act = () => SqlQuoting.ValidateTdeAlgorithm(algo);
        act.Should().Throw<ArgumentException>();
    }

    // ── Date literal ───────────────────────────────────────

    [Theory]
    [InlineData("2030-01-15")]
    [InlineData("20300115")]
    [InlineData("2030/01/15")]
    public void ValidateDateLiteral_AllowsValidDates(string date)
    {
        SqlQuoting.ValidateDateLiteral(date).Should().Be(date);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2030-13-99")]
    [InlineData("2030-01-15' OR '1'='1")]
    public void ValidateDateLiteral_RejectsInvalid(string date)
    {
        var act = () => SqlQuoting.ValidateDateLiteral(date);
        act.Should().Throw<ArgumentException>();
    }

    // ── AG allowlists ──────────────────────────────────────

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT")]
    [InlineData("asynchronous_commit")] // case-insensitive
    public void ValidateAvailabilityMode_AllowsKnownModes(string mode)
    {
        SqlQuoting.ValidateAvailabilityMode(mode).Should().Be(mode);
    }

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT; DROP DATABASE [x]")]
    [InlineData("AUTOMATIC")]
    [InlineData("")]
    public void ValidateAvailabilityMode_RejectsUnknownModes(string mode)
    {
        var act = () => SqlQuoting.ValidateAvailabilityMode(mode);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("AUTOMATIC")]
    [InlineData("manual")] // case-insensitive
    public void ValidateSeedingMode_AllowsKnownModes(string mode)
    {
        SqlQuoting.ValidateSeedingMode(mode).Should().Be(mode);
    }

    [Theory]
    [InlineData("AUTOMATIC) WITH (X = 1")]
    [InlineData("SYNCHRONOUS_COMMIT")]
    public void ValidateSeedingMode_RejectsUnknownModes(string mode)
    {
        var act = () => SqlQuoting.ValidateSeedingMode(mode);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("NO")]
    [InlineData("READ_ONLY")]
    [InlineData("all")] // case-insensitive
    public void ValidateAllowConnections_AllowsKnownValues(string value)
    {
        SqlQuoting.ValidateAllowConnections(value).Should().Be(value);
    }

    [Theory]
    [InlineData("ALL) -- injection")]
    [InlineData("YES")]
    public void ValidateAllowConnections_RejectsUnknownValues(string value)
    {
        var act = () => SqlQuoting.ValidateAllowConnections(value);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("AES")]
    [InlineData("rc4")] // case-insensitive
    [InlineData("AES RC4")]
    public void ValidateEndpointAlgorithm_AllowsKnownAlgorithms(string algorithm)
    {
        SqlQuoting.ValidateEndpointAlgorithm(algorithm).Should().Be(algorithm);
    }

    [Theory]
    [InlineData("AES) ROLE = ALL (")]
    [InlineData("AES_256")] // TDE algorithm, not an endpoint algorithm
    public void ValidateEndpointAlgorithm_RejectsUnknownAlgorithms(string algorithm)
    {
        var act = () => SqlQuoting.ValidateEndpointAlgorithm(algorithm);
        act.Should().Throw<ArgumentException>();
    }

    // ── AG pod FQDN ────────────────────────────────────────

    [Fact]
    public void BuildAgPodFqdn_IncludesHeadlessService()
    {
        SqlQuoting.BuildAgPodFqdn("my-ag", 0, "demo")
            .Should().Be("my-ag-0.my-ag-headless.demo.svc.cluster.local");
    }

    [Fact]
    public void BuildAgPodFqdn_ExplicitHeadlessService()
    {
        SqlQuoting.BuildAgPodFqdn("my-ag", 2, "custom-headless", "demo")
            .Should().Be("my-ag-2.custom-headless.demo.svc.cluster.local");
    }
}
