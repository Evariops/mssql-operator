using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class SaPasswordConvergerTests
{
    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlConnectionFactory _connFactory = new();
    private readonly SaPasswordConverger _converger;

    public SaPasswordConvergerTests()
    {
        _converger = new SaPasswordConverger(NullLogger<SaPasswordConverger>.Instance, _k8s, _connFactory,
            new FakeSqlUserManager(), new FakeEventPublisher());
    }

    private void Pod(string name, string ip)
        => _k8s.SetGet(name, "default", new V1Pod { Metadata = new V1ObjectMeta { Name = name }, Status = new V1PodStatus { PodIP = ip } });

    [Theory]
    [InlineData(false, "sql-0", "sql.default.svc.cluster.local")]
    [InlineData(true, "sql-0", "sql-0.sql-headless.default.svc.cluster.local")]
    public async Task LoginThroughThePodIp_ValidatesTheCertificateAgainstItsName(bool availabilityGroup, string pod, string expected)
    {
        // The pod IP matches no SAN of the certificate: with Encrypt=Strict, every login
        // failed the handshake and the convergence stayed Pending forever.
        IKubernetesObject<V1ObjectMeta> owner = availabilityGroup
            ? new SqlAvailabilityGroup { Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Uid = "u" } }
            : new SqlInstance { Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Uid = "u" } };
        Pod(pod, "10.0.0.5");

        var outcome = await _converger.ConvergeAsync(owner, [pod], "pw", CancellationToken.None);

        Assert.Equal(SaConvergence.Converged, outcome);
        Assert.Equal([("10.0.0.5", (string?)expected)], _connFactory.ProbeCalls);
    }
}
