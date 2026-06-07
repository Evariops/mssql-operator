using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlStoredProcedure", PluralName = "sqlstoredprocedures")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Deployed", ".status.deployedProcedures", "integer")]
[PrinterColumn("Desired", ".status.desiredProcedures", "integer")]
[PrinterColumn("Targets", ".status.targets", "integer", priority: 1)]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlStoredProcedure : CustomKubernetesEntity<SqlStoredProcedureSpec, SqlStoredProcedureStatus>
{
}
