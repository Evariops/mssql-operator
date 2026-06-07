using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlUser", PluralName = "sqlusers")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Ready", ".status.readyTargets", "integer")]
[PrinterColumn("Targets", ".status.totalTargets", "integer")]
[PrinterColumn("Expired", ".status.expired", "boolean", priority: 1)]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlUser : CustomKubernetesEntity<SqlUserSpec, SqlUserStatus>
{
}
