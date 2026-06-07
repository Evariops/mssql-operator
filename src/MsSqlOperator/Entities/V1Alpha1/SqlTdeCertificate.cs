using k8s.Models;
using MsSqlOperator.Operator.Abstractions;

namespace MsSqlOperator.Entities.V1Alpha1;

[KubernetesEntity(Group = "mssql.operator", ApiVersion = "v1alpha1", Kind = "SqlTdeCertificate", PluralName = "sqltdecertificates")]
[PrinterColumn("Phase", ".status.phase")]
[PrinterColumn("Encrypted", ".status.encryptedDatabases", "integer")]
[PrinterColumn("Pending", ".status.pendingDatabases", "integer")]
[PrinterColumn("Rotation", ".status.rotationPhase", priority: 1)]
[PrinterColumn("Age", ".metadata.creationTimestamp", "date")]
public class SqlTdeCertificate : CustomKubernetesEntity<SqlTdeCertificateSpec, SqlTdeCertificateStatus>
{
}
