using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlAgentJob", PluralName = "sqlagentjobs")]
public class SqlAgentJob : CustomKubernetesEntity<SqlAgentJobSpec, SqlAgentJobStatus>
{
}
