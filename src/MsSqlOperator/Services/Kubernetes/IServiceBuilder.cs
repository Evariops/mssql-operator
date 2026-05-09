using k8s.Models;
using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Services.Kubernetes;

public interface IServiceBuilder
{
    V1Service BuildClusterIp(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference owner);
    V1Service BuildHeadless(string name, string ns, Dictionary<string, string> selector, int port, V1OwnerReference owner);
    V1Service Build(string name, string ns, Dictionary<string, string> selector, ServiceSpec serviceSpec, V1OwnerReference owner);
}
