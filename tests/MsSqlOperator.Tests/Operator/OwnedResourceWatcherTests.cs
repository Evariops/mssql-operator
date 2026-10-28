using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Operator.Runtime;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Operator;

public class OwnedResourceWatcherTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // ── MapToParent: the pure ownerReference → parent-key mapping ──

    [Fact]
    public void MapToParent_PicksControllerOwnerReference()
    {
        var obj = Json("""
        {"metadata":{"name":"sql1-0","namespace":"prod","ownerReferences":[
            {"kind":"SqlInstance","name":"sql1","controller":true,"uid":"u1"}]}}
        """);

        Assert.Equal(("SqlInstance", "prod/sql1"), OwnedResourceWatcher.MapToParent(obj));
    }

    [Fact]
    public void MapToParent_IgnoresNonControllerOwners()
    {
        // Only a non-controller owner reference → no authoritative parent.
        var obj = Json("""
        {"metadata":{"name":"x","namespace":"prod","ownerReferences":[
            {"kind":"SomethingElse","name":"y","controller":false}]}}
        """);

        Assert.Null(OwnedResourceWatcher.MapToParent(obj));
    }

    [Fact]
    public void MapToParent_ReturnsNull_WhenNoOwnerReferences()
    {
        Assert.Null(OwnedResourceWatcher.MapToParent(Json("""{"metadata":{"name":"x","namespace":"prod"}}""")));
    }

    // ── Routing + dedup + status-only filtering ──
    // The raw IKubernetes client is only used by RunAsync (never called here), so null! is safe.

    private static (OwnedResourceWatcher Watcher, FakeEntityRuntime Runtime) Build(string entityKind = "SqlInstance")
    {
        var runtime = new FakeEntityRuntime(entityKind);
        var watcher = new OwnedResourceWatcher(
            [runtime], null!, new OperatorMetrics(), NullLogger<OwnedResourceWatcher>.Instance);
        return (watcher, runtime);
    }

    private static JsonElement Sts(string name, long? generation)
    {
        var gen = generation is { } g ? "\"generation\":" + g + "," : "";
        return Json("{\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"prod\"," + gen +
                    "\"ownerReferences\":[{\"kind\":\"SqlInstance\",\"name\":\"" + name +
                    "\",\"controller\":true}]}}");
    }

    private static JsonElement Labelled(string name, string ownerKind, string ownerUid)
        => Json("{\"metadata\":{\"name\":\"" + name + "\",\"namespace\":\"prod\",\"labels\":{" +
                "\"mssql-operator.evariops.com/owner-kind\":\"" + ownerKind + "\"," +
                "\"mssql-operator.evariops.com/owner-uid\":\"" + ownerUid + "\"}}}");

    [Fact]
    public void Route_KeySecretDeleted_ReconcilesItsOwnerFoundByUid()
    {
        // A key Secret names its owner by UID only (it outlives the CR by design).
        var (watcher, runtime) = Build();
        runtime.KeysByUid["uid-1"] = "prod/sql1";

        watcher.Route(OwnedResourceWatcher.LabelledSecret, "DELETED", Labelled("sql1-machine-key", "SqlInstance", "uid-1"));

        Assert.Equal(["prod/sql1"], runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_LabelledPvc_OfAnUnknownOwner_IsIgnored()
    {
        var (watcher, runtime) = Build();

        watcher.Route(OwnedResourceWatcher.LabelledPvc, "DELETED", Labelled("data-sql1-0", "SqlInstance", "uid-gone"));

        Assert.Empty(runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_EnqueuesParent_OnDelete()
    {
        var (watcher, runtime) = Build();

        watcher.Route("StatefulSet", "DELETED", Sts("sql1", 3));

        Assert.Equal(["prod/sql1"], runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_EnqueuesParent_OnSpecChange()
    {
        var (watcher, runtime) = Build();

        // Seed at generation 1 (no enqueue), then a generation bump = a real spec change.
        watcher.Seed("StatefulSet", [Sts("sql1", 1)]);
        watcher.Route("StatefulSet", "MODIFIED", Sts("sql1", 2));

        Assert.Equal(["prod/sql1"], runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_IgnoresStatusOnlyChange_SameGeneration()
    {
        var (watcher, runtime) = Build();

        // Seeded generation 5; a status update keeps generation 5 → no parent reconcile.
        watcher.Seed("StatefulSet", [Sts("sql1", 5)]);
        watcher.Route("StatefulSet", "MODIFIED", Sts("sql1", 5));

        Assert.Empty(runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_EnqueuesEverySpecChange_WhenNoGeneration()
    {
        var (watcher, runtime) = Build();

        // ConfigMaps carry no generation: every change must be treated as real.
        var cm = Json("""{"metadata":{"name":"sql1-mssql-conf","namespace":"prod","ownerReferences":[{"kind":"SqlInstance","name":"sql1","controller":true}]}}""");
        watcher.Seed("ConfigMap", [cm]);
        watcher.Route("ConfigMap", "MODIFIED", cm);

        Assert.Equal(["prod/sql1"], runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_DoesNotEnqueue_WhenParentKindNotManaged()
    {
        // Owner kind SqlInstance, but only an AG runtime is registered → no route target.
        var (watcher, runtime) = Build("SqlAvailabilityGroup");

        watcher.Route("StatefulSet", "DELETED", Sts("sql1", 1));

        Assert.Empty(runtime.EnqueueCalls);
    }

    [Fact]
    public void Route_DoesNotEnqueue_WhenNoControllerOwner()
    {
        var (watcher, runtime) = Build();
        var orphan = Json("""{"metadata":{"name":"x","namespace":"prod"}}""");

        watcher.Route("StatefulSet", "MODIFIED", orphan);

        Assert.Empty(runtime.EnqueueCalls);
    }

    [Fact]
    public void IsHealthy_TrueBeforeStarted()
    {
        var (watcher, _) = Build();
        Assert.True(watcher.IsHealthy());
    }
}

/// <summary>SQL pods are routed to their CR by label, and re-enqueue it on a real change.</summary>
public class SqlPodRoutingTests
{
    private static System.Text.Json.JsonElement Pod(string label, string owner, string ready, string? deleting = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = $"{owner}-0",
            ["namespace"] = "db",
            ["uid"] = "u1",
            ["labels"] = new Dictionary<string, string> { [label] = owner },
        };
        if (deleting is not null) metadata["deletionTimestamp"] = deleting;
        var pod = new Dictionary<string, object?>
        {
            ["metadata"] = metadata,
            ["spec"] = new Dictionary<string, object?> { ["nodeName"] = "n1" },
            ["status"] = new Dictionary<string, object?>
            {
                ["phase"] = "Running",
                ["conditions"] = new[] { new Dictionary<string, string> { ["type"] = "Ready", ["status"] = ready } },
            },
        };
        return System.Text.Json.JsonSerializer.SerializeToElement(pod);
    }

    [Theory]
    [InlineData("Pod (instance)", "mssql-operator.evariops.com/instance", "SqlInstance")]
    [InlineData("Pod (ag)", "mssql-operator.evariops.com/ag", "SqlAvailabilityGroup")]
    public void Pod_IsRoutedToItsCrByLabel(string childKind, string label, string parentKind)
        => Assert.Equal((parentKind, "db/sales"), MsSqlOperator.Operator.Runtime.OwnedResourceWatcher.MapToParent(childKind, Pod(label, "sales", "True")));

    [Fact]
    public void Signature_MovesWithReadinessAndDeletion_NotWithAnythingElse()
    {
        const string label = "mssql-operator.evariops.com/ag";
        var ready = MsSqlOperator.Operator.Runtime.OwnedResourceWatcher.SignatureOf(Pod(label, "sales", "True"));

        Assert.Equal(ready, MsSqlOperator.Operator.Runtime.OwnedResourceWatcher.SignatureOf(Pod(label, "sales", "True")));
        Assert.NotEqual(ready, MsSqlOperator.Operator.Runtime.OwnedResourceWatcher.SignatureOf(Pod(label, "sales", "False")));
        Assert.NotEqual(ready, MsSqlOperator.Operator.Runtime.OwnedResourceWatcher.SignatureOf(Pod(label, "sales", "True", "2026-10-28T08:00:00Z")));
    }
}
