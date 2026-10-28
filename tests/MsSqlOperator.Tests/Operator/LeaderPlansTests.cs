using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using k8s.Models;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Operator.ViewApi;
using MsSqlOperator.Services.Actuation;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Operator;

/// <summary><c>kubectl get operatorplans</c> answered by the follower came back empty.</summary>
public class LeaderPlansTests
{
    private const string Ns = "mssql-operator";
    private readonly FakeKubernetesClient _k8s = new();
    private readonly OperatorOptions _options = new() { Namespace = Ns, Identity = "op-a" };
    private readonly PlanStore _local = new();

    private static readonly PlannedAction Fence = new("Failover:chaos:3:ab12", "Failover", "Disruptive", "chaos",
        "fence chaos-1, then fail chaos over to chaos-0", "Withheld", "Plan mode", false, DateTimeOffset.UnixEpoch);

    private void LeaderIs(string? holder, string? podIp = "10.0.0.7")
    {
        _k8s.SetGet(_options.LeaseName, Ns, new V1Lease { Spec = new V1LeaseSpec { HolderIdentity = holder } });
        if (holder is not null)
            _k8s.SetGet(holder, Ns, new V1Pod { Status = new V1PodStatus { PodIP = podIp } });
    }

    private OperatorPlansProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> leader)
        => new(_local, new LeaderPlans(_k8s, _options, Certificate, 9443, () => new Handler(leader)));

    [Fact]
    public async Task Follower_ServesThePlansOfTheLeader()
    {
        LeaderIs("op-b");
        Uri? asked = null;
        var provider = Provider(request =>
        {
            asked = request.RequestUri;
            return Json([new RemotePlan("SqlAvailabilityGroup/mssql-chaos/chaos", [Fence])]);
        });

        var items = await provider.ListAsync("mssql-chaos", null, CancellationToken.None);

        Assert.Equal(new Uri("https://10.0.0.7:9443" + LeaderPlans.Path), asked);
        var item = Assert.Single(items);
        Assert.Equal("Withheld", item["decision"]);
        Assert.Equal(Fence.Id, item["id"]);
        Assert.Equal("SqlAvailabilityGroup/chaos", item["resource"]);
    }

    [Fact]
    public async Task Leader_ServesItsOwnPlans()
    {
        LeaderIs("op-a");
        _local.Record(PlanStore.Key("SqlAvailabilityGroup", "mssql-chaos", "chaos"), Fence);
        var provider = Provider(_ => throw new InvalidOperationException("the leader does not ask itself"));

        Assert.Single(await provider.ListAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task LeaderNotAnswering_IsSaid_NotAnEmptyPlan()
    {
        LeaderIs("op-b");
        var provider = Provider(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<ViewUnavailableException>(() => provider.ListAsync(null, null, CancellationToken.None));
        Assert.Contains("op-b", ex.Message);
    }

    [Fact]
    public async Task NoLeader_IsSaid()
    {
        LeaderIs(null);

        await Assert.ThrowsAsync<ViewUnavailableException>(
            () => Provider(_ => Json([])).ListAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutLeaderElection_ThePlansAreLocal()
    {
        _options.LeaderElection = false;
        _local.Record(PlanStore.Key("SqlAvailabilityGroup", "mssql-chaos", "chaos"), Fence);

        Assert.Single(await Provider(_ => throw new InvalidOperationException()).ListAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public void ThePeerMustPresentTheServingCertificate()
    {
        using var other = SelfSigned("other");

        Assert.True(LeaderPlans.SameCertificate(Certificate(), Certificate()));
        Assert.False(LeaderPlans.SameCertificate(other, Certificate()));
        Assert.False(LeaderPlans.SameCertificate(null, Certificate()));
    }

    private static readonly X509Certificate2 Serving = SelfSigned("mssql-operator-webhook.mssql-operator.svc");

    private static X509Certificate2 Certificate() => Serving;

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static HttpResponseMessage Json(List<RemotePlan> plans)
        => new(HttpStatusCode.OK) { Content = new StringContent(ViewApiJson.Serialize(plans)) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
