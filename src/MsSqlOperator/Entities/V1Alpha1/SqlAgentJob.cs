using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlAgentJob", PluralName = "sqlagentjobs")]
public class SqlAgentJob : CustomKubernetesEntity<SqlAgentJobSpec, SqlAgentJobStatus>
{
}
