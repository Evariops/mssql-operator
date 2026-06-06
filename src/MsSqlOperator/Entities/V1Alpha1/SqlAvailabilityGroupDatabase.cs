using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlAvailabilityGroupDatabase", PluralName = "sqlavailabilitygroupdatabases")]
[KubernetesEntityShortNames("sagdb")]
public class SqlAvailabilityGroupDatabase : CustomKubernetesEntity<SqlAvailabilityGroupDatabaseSpec, SqlAvailabilityGroupDatabaseStatus>
{
}
