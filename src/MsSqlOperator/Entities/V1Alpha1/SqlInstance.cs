using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlInstance", PluralName = "sqlinstances")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Version", ".status.version", priority: 1)]
[PrinterColumn("Endpoint", ".status.endpoint", priority: 1)]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlInstance : CustomKubernetesEntity<SqlInstanceSpec, SqlInstanceStatus>
{
}
