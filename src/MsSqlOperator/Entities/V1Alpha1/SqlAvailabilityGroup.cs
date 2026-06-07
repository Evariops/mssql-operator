using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlAvailabilityGroup", PluralName = "sqlavailabilitygroups")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Primary", ".status.primaryReplica", priority: 1)]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlAvailabilityGroup : CustomKubernetesEntity<SqlAvailabilityGroupSpec, SqlAvailabilityGroupStatus>
{
}
