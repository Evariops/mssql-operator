using System.ComponentModel.DataAnnotations;

namespace MsSqlOperator.Entities.Common;

/// <summary>The certificate SQL Server presents to its clients — the operator included,
/// which can then authenticate the server (Helm: sqlConnection.caSecretName, trustServerCertificate
/// false).</summary>
public class TlsSpec
{
    /// <summary>A <c>kubernetes.io/tls</c> Secret (<c>tls.crt</c>, <c>tls.key</c>) in the resource's
    /// namespace. Its SANs must cover the names clients connect to: <c>&lt;instance&gt;.&lt;namespace&gt;.svc.cluster.local</c>
    /// for an instance; <c>*.&lt;ag&gt;-headless.&lt;namespace&gt;.svc.cluster.local</c> and the listener
    /// Services for an availability group. SQL Server reads it at startup: a renewal takes effect at
    /// the next restart.</summary>
    [RegularExpression("^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$")]
    [MaxLength(253)]
    public string SecretName { get; set; } = string.Empty;

    /// <summary>Refuse unencrypted client connections (mssql.conf <c>network.forceencryption</c>).</summary>
    public bool ForceEncryption { get; set; }
}
