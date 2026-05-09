using k8s.Models;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.Kubernetes;

public interface IStatefulSetBuilder
{
    V1StatefulSet Build(SqlInstance instance, string saPassword);
    V1StatefulSet BuildForAg(SqlAvailabilityGroup ag, string saPassword);
}
