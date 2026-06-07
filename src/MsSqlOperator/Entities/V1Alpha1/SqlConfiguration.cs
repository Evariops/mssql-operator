using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlConfiguration", PluralName = "sqlconfigurations")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Applied", ".status.appliedInstances", "integer")]
[PrinterColumn("Matched", ".status.matchedInstances", "integer")]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlConfiguration : CustomKubernetesEntity<SqlConfigurationSpec, SqlConfigurationStatus>
{
}
