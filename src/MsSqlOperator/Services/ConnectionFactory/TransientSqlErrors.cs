using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>
/// Classifies SQL Server / Azure SQL errors as transient (worth retrying). These typically occur
/// during an AG failover, a pod restart, a transport reset or a brief throttling window — the very
/// situations the operator must ride through instead of parking a CR in <c>Failed</c>.
/// </summary>
public static class TransientSqlErrors
{
    // Error numbers known to be transient: connection/transport resets, login throttling,
    // database unavailable during failover, deadlock victims, and command timeouts.
    private static readonly HashSet<int> TransientNumbers =
    [
        -2,     // command/connection timeout
        20,     // instance does not support encryption / transport-level on handshake
        64,     // connection terminated (transport)
        233,    // no process on the other end of the pipe
        1205,   // deadlock victim
        10053,  // transport-level error: established connection aborted
        10054,  // transport-level error: existing connection forcibly closed
        10060,  // network/connection timeout
        10928,  // resource limit reached
        10929,  // resource limit (server busy)
        40143,  // connection terminated (service error)
        40197,  // service error processing request (failover in progress)
        40501,  // service busy
        40613,  // database currently unavailable (failover/restart)
        4060,   // cannot open database (login race during recovery)
        49918,  // cannot process request, not enough resources
        49919,  // cannot process create/update request
        49920,  // cannot process request, too many operations
    ];

    public static bool IsTransient(Exception ex) => ex switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)),
        TimeoutException => true,
        _ => false,
    };
}
