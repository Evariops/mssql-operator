using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class StatefulSetRecreationTests
{
    private readonly FakeKubernetesClient _k8s = new();

    private static V1StatefulSet Sts(string logSize) => new()
    {
        Metadata = new V1ObjectMeta { Name = "ag", NamespaceProperty = "default" },
        Spec = new V1StatefulSetSpec
        {
            Replicas = 1,
            VolumeClaimTemplates =
            [
                new V1PersistentVolumeClaim
                {
                    Metadata = new V1ObjectMeta { Name = "log" },
                    Spec = new V1PersistentVolumeClaimSpec
                    {
                        Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(logSize) } },
                    },
                },
            ],
        },
    };

    private V1PersistentVolumeClaim Pvc(string size)
    {
        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = "log-ag-0", NamespaceProperty = "default" },
            Spec = new V1PersistentVolumeClaimSpec
            {
                Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) } },
            },
        };
        _k8s.SetGet("log-ag-0", "default", pvc);
        return pvc;
    }

    [Fact]
    public async Task LargerTemplate_GrowsTheClaims()
    {
        Pvc("4Gi");

        var grown = await StatefulSetRecreation.ExpandVolumeClaimsAsync(_k8s, NullLogger.Instance, Sts("4Gi"), Sts("5Gi"), CancellationToken.None);

        Assert.True(grown);
        Assert.Single(_k8s.PatchCalls);
    }

    [Fact]
    public async Task ClaimAlreadyGrownPastTheTemplate_IsNotShrunk()
    {
        // The log volume grew to 6Gi on its own; a template raised to 5Gi asked the API
        // server to shrink it, which it refuses — the reconcile failed on every pass.
        var pvc = Pvc("6Gi");

        await StatefulSetRecreation.ExpandVolumeClaimsAsync(_k8s, NullLogger.Instance, Sts("4Gi"), Sts("5Gi"), CancellationToken.None);

        Assert.Empty(_k8s.PatchCalls);
        Assert.Equal("6Gi", pvc.Spec.Resources.Requests["storage"].ToString());
    }
}
