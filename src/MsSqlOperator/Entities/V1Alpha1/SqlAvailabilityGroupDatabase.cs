using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlAvailabilityGroupDatabase", PluralName = "sqlavailabilitygroupdatabases")]
[KubernetesEntityShortNames("sagdb")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("AG", ".status.availabilityGroupName")]
[PrinterColumn("Database", ".status.databaseName")]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlAvailabilityGroupDatabase : CustomKubernetesEntity<SqlAvailabilityGroupDatabaseSpec, SqlAvailabilityGroupDatabaseStatus>
{
}
