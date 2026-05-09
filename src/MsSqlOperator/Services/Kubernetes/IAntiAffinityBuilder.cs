using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public interface IAntiAffinityBuilder
{
    V1Affinity? Build(AntiAffinitySpec? spec, Dictionary<string, string> selfLabels);
}
