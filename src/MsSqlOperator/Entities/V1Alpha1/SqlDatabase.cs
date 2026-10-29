using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

/// <summary>
/// A user database on a SqlInstance or an availability group: created when missing, adopted when it
/// exists, its options kept as declared. On an AG it is created on the primary and joined through a
/// SqlAvailabilityGroupDatabase the operator manages. Deleting the resource keeps the database
/// unless <c>deletionPolicy: Delete</c>.
/// </summary>
[KubernetesEntity(Group = "mssql-operator.evariops.com", ApiVersion = "v1alpha1", Kind = "SqlDatabase", PluralName = "sqldatabases")]
[KubernetesEntityShortNames("sqldb")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Server", ".status.server")]
[PrinterColumn("Database", ".spec.databaseName")]
[PrinterColumn("Adopted", ".status.adopted", "boolean")]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlDatabase : CustomKubernetesEntity<SqlDatabaseSpec, SqlDatabaseStatus>
{
}
