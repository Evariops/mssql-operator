using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlConfiguration", PluralName = "sqlconfigurations")]
public class SqlConfiguration : CustomKubernetesEntity<SqlConfigurationSpec, SqlConfigurationStatus>
{
}
