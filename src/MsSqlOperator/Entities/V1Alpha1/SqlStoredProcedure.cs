using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlStoredProcedure", PluralName = "sqlstoredprocedures")]
public class SqlStoredProcedure : CustomKubernetesEntity<SqlStoredProcedureSpec, SqlStoredProcedureStatus>
{
}
