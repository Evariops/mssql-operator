using System.Text;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

/// <summary>Key material is never overwritten.</summary>
public class KeyMaterialVersionsTests
{
    private static readonly DateTime At = new(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void AppliedSaPassword_ThePreviousOneIsKept()
    {
        var data = new Dictionary<string, byte[]> { ["password"] = "first"u8.ToArray() };

        var next = SaPasswordConverger.WithHistory(data, "second"u8.ToArray(), At);

        Assert.Equal("second", Encoding.UTF8.GetString(next["password"]));
        Assert.Equal("first", Encoding.UTF8.GetString(next["password.20261028093000"]));
    }

    [Fact]
    public void AppliedSaPassword_Unchanged_AddsNoHistory()
        => Assert.Single(SaPasswordConverger.WithHistory(
            new Dictionary<string, byte[]> { ["password"] = "same"u8.ToArray() }, "same"u8.ToArray(), At));

    [Fact]
    public void AppliedSaPassword_HistoryIsBounded_TheOldestGoesFirst()
    {
        var data = new Dictionary<string, byte[]> { ["password"] = "p0"u8.ToArray() };
        for (var i = 1; i <= SaPasswordConverger.MaxHistory + 3; i++)
            data = SaPasswordConverger.WithHistory(data, Encoding.UTF8.GetBytes($"p{i}"), At.AddMinutes(i));

        Assert.Equal(SaPasswordConverger.MaxHistory, data.Keys.Count(k => k.StartsWith("password.", StringComparison.Ordinal)));
        Assert.DoesNotContain(data.Values, v => Encoding.UTF8.GetString(v) == "p0");
    }

    [Fact]
    public async Task GeneratedPassword_IsAnImmutableSecret()
    {
        var k8s = new FakeKubernetesClient();
        var owner = new SqlInstance { Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "db", Uid = "u" } };

        await KeyMaterialSecrets.GetOrCreateMasterKeyPasswordAsync(k8s, owner, "db", NullLogger.Instance, CancellationToken.None);

        Assert.True(Assert.Single(k8s.Created<V1Secret>()).Immutable);
    }
}
