using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

public class SqlQuotingTests
{
    // ── Identifier quoting ─────────────────────────────────

    [Fact]
    public void QuoteIdentifier_WrapsInBrackets()
    {
        Assert.Equal("[my_login]", SqlQuoting.QuoteIdentifier("my_login"));
    }

    [Fact]
    public void QuoteIdentifier_EscapesClosingBracket()
    {
        Assert.Equal("[my]]name]", SqlQuoting.QuoteIdentifier("my]name"));
        Assert.Equal("[a]]b]]c]", SqlQuoting.QuoteIdentifier("a]b]c"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void QuoteIdentifier_RejectsNullOrWhitespace(string? value)
    {
        var act = () => SqlQuoting.QuoteIdentifier(value!);
        Assert.ThrowsAny<ArgumentException>(act);
    }

    // ── String literal quoting ─────────────────────────────

    [Fact]
    public void QuoteStringLiteral_EscapesSingleQuote()
    {
        Assert.Equal("'it''s'", SqlQuoting.QuoteStringLiteral("it's"));
        Assert.Equal("'a''b''c'", SqlQuoting.QuoteStringLiteral("a'b'c"));
    }

    [Fact]
    public void QuoteNStringLiteral_PrefixesNAndEscapes()
    {
        Assert.Equal("N'café'", SqlQuoting.QuoteNStringLiteral("café"));
        Assert.Equal("N'd''oh'", SqlQuoting.QuoteNStringLiteral("d'oh"));
    }

    // ── Permission allowlists ──────────────────────────────

    [Theory]
    [InlineData("VIEW SERVER STATE")]
    [InlineData("CONTROL SERVER")]
    [InlineData("connect sql")] // case-insensitive
    public void ValidateServerPermission_AllowsKnownPermissions(string permission)
    {
        Assert.Equal(permission, SqlQuoting.ValidateServerPermission(permission));
    }

    [Theory]
    [InlineData("DROP DATABASE")]
    [InlineData("SELECT")] // schema-level, not a server permission
    [InlineData("VIEW SERVER STATE; DROP TABLE x")] // injection attempt
    public void ValidateServerPermission_RejectsUnknown(string permission)
    {
        var act = () => SqlQuoting.ValidateServerPermission(permission);
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("execute")]
    public void ValidateSchemaPermission_AllowsKnownPermissions(string permission)
    {
        Assert.Equal(permission, SqlQuoting.ValidateSchemaPermission(permission));
    }

    [Theory]
    [InlineData("CONTROL SERVER")] // server-level, not schema
    [InlineData("SELECT--")]
    public void ValidateSchemaPermission_RejectsUnknown(string permission)
    {
        var act = () => SqlQuoting.ValidateSchemaPermission(permission);
        Assert.Throws<ArgumentException>(act);
    }

    // ── TDE algorithm allowlist ────────────────────────────

    [Theory]
    [InlineData("AES_256")]
    [InlineData("aes_128")]
    public void ValidateTdeAlgorithm_AllowsKnown(string algo)
    {
        Assert.Equal(algo, SqlQuoting.ValidateTdeAlgorithm(algo));
    }

    [Theory]
    [InlineData("AES_512")]
    [InlineData("AES_256; DROP DATABASE x")]
    public void ValidateTdeAlgorithm_RejectsUnknown(string algo)
    {
        var act = () => SqlQuoting.ValidateTdeAlgorithm(algo);
        Assert.Throws<ArgumentException>(act);
    }

    // ── Date literal ───────────────────────────────────────

    [Theory]
    [InlineData("2030-01-15")]
    [InlineData("20300115")]
    [InlineData("2030/01/15")]
    public void ValidateDateLiteral_AllowsValidDates(string date)
    {
        Assert.Equal(date, SqlQuoting.ValidateDateLiteral(date));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2030-13-99")]
    [InlineData("2030-01-15' OR '1'='1")]
    public void ValidateDateLiteral_RejectsInvalid(string date)
    {
        var act = () => SqlQuoting.ValidateDateLiteral(date);
        Assert.Throws<ArgumentException>(act);
    }

    // ── AG allowlists ──────────────────────────────────────

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT")]
    [InlineData("asynchronous_commit")] // case-insensitive
    public void ValidateAvailabilityMode_AllowsKnownModes(string mode)
    {
        Assert.Equal(mode, SqlQuoting.ValidateAvailabilityMode(mode));
    }

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT; DROP DATABASE [x]")]
    [InlineData("AUTOMATIC")]
    [InlineData("")]
    public void ValidateAvailabilityMode_RejectsUnknownModes(string mode)
    {
        var act = () => SqlQuoting.ValidateAvailabilityMode(mode);
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData("AUTOMATIC")]
    [InlineData("manual")] // case-insensitive
    public void ValidateSeedingMode_AllowsKnownModes(string mode)
    {
        Assert.Equal(mode, SqlQuoting.ValidateSeedingMode(mode));
    }

    [Theory]
    [InlineData("AUTOMATIC) WITH (X = 1")]
    [InlineData("SYNCHRONOUS_COMMIT")]
    public void ValidateSeedingMode_RejectsUnknownModes(string mode)
    {
        var act = () => SqlQuoting.ValidateSeedingMode(mode);
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData("NO")]
    [InlineData("READ_ONLY")]
    [InlineData("all")] // case-insensitive
    public void ValidateAllowConnections_AllowsKnownValues(string value)
    {
        Assert.Equal(value, SqlQuoting.ValidateAllowConnections(value));
    }

    [Theory]
    [InlineData("ALL) -- injection")]
    [InlineData("YES")]
    public void ValidateAllowConnections_RejectsUnknownValues(string value)
    {
        var act = () => SqlQuoting.ValidateAllowConnections(value);
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData("AES")]
    [InlineData("rc4")] // case-insensitive
    [InlineData("AES RC4")]
    public void ValidateEndpointAlgorithm_AllowsKnownAlgorithms(string algorithm)
    {
        Assert.Equal(algorithm, SqlQuoting.ValidateEndpointAlgorithm(algorithm));
    }

    [Theory]
    [InlineData("AES) ROLE = ALL (")]
    [InlineData("AES_256")] // TDE algorithm, not an endpoint algorithm
    public void ValidateEndpointAlgorithm_RejectsUnknownAlgorithms(string algorithm)
    {
        var act = () => SqlQuoting.ValidateEndpointAlgorithm(algorithm);
        Assert.Throws<ArgumentException>(act);
    }

    // ── AG pod FQDN ────────────────────────────────────────

    [Fact]
    public void BuildAgPodFqdn_IncludesHeadlessService()
    {
        Assert.Equal("my-ag-0.my-ag-headless.demo.svc.cluster.local", SqlQuoting.BuildAgPodFqdn("my-ag", 0, "demo"));
    }

    [Fact]
    public void BuildAgPodFqdn_ExplicitHeadlessService()
    {
        Assert.Equal("my-ag-2.custom-headless.demo.svc.cluster.local", SqlQuoting.BuildAgPodFqdn("my-ag", 2, "custom-headless", "demo"));
    }
}
