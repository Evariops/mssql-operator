using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>
/// Classifies exceptions raised while opening or using a SQL Server connection as transient (worth
/// retrying) or permanent. Read from the <see cref="SqlErrors"/> taxonomy.
/// </summary>
public static class TransientSqlErrors
{
    public static bool IsTransient(Exception ex) => ex switch
    {
        SqlException sql => SqlErrors.IsTransient(SqlErrors.Classify(sql)),
        TimeoutException => true,
        _ => false,
    };
}
