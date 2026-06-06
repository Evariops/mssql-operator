using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlInstance", PluralName = "sqlinstances")]
public class SqlInstance : CustomKubernetesEntity<SqlInstanceSpec, SqlInstanceStatus>
{
}
