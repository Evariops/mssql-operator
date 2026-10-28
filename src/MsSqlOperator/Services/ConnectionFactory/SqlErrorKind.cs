using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>What a SQL Server error means for the caller, read from its number rather
/// than from its text (which changes with the language and the version).</summary>
public enum SqlErrorKind
{
    Other,
    /// <summary>Transport, throttling, database briefly unavailable: worth retrying.</summary>
    Transient,
    /// <summary>1222: LOCK_TIMEOUT expired — someone else holds the object.</summary>
    LockTimeout,
    /// <summary>1205: chosen as the deadlock victim.</summary>
    Deadlock,
    /// <summary>-2: the command timed out.</summary>
    CommandTimeout,
    LoginFailed,
    PermissionDenied,
    AlreadyExists,
    NotFound,
    /// <summary>The database or replica is not accessible in its current AG role.</summary>
    AgUnavailable,
}

/// <summary>
/// The error taxonomy. The table covers the numbers documented by SQL Server; lab runs
/// confirm and extend it — DMK password, SMK decryption and log-backup-required errors among them.
/// </summary>
public static class SqlErrors
{
    private static readonly Dictionary<int, SqlErrorKind> Kinds = new()
    {
        [-2] = SqlErrorKind.CommandTimeout,
        [1222] = SqlErrorKind.LockTimeout,
        [1205] = SqlErrorKind.Deadlock,

        [18456] = SqlErrorKind.LoginFailed, // login failed
        [18452] = SqlErrorKind.LoginFailed, // untrusted domain
        [18486] = SqlErrorKind.LoginFailed, // account locked out
        [18487] = SqlErrorKind.LoginFailed, // password expired
        [18488] = SqlErrorKind.LoginFailed, // password must be changed

        [229] = SqlErrorKind.PermissionDenied, // permission denied on object
        [230] = SqlErrorKind.PermissionDenied, // permission denied on column
        [262] = SqlErrorKind.PermissionDenied, // permission denied in database
        [297] = SqlErrorKind.PermissionDenied, // user does not have permission
        [300] = SqlErrorKind.PermissionDenied, // permission denied on object or securable
        [916] = SqlErrorKind.PermissionDenied, // principal cannot access the database
        [15247] = SqlErrorKind.PermissionDenied, // no permission to perform this action

        [2714] = SqlErrorKind.AlreadyExists, // object already exists
        [1801] = SqlErrorKind.AlreadyExists, // database already exists
        [15023] = SqlErrorKind.AlreadyExists, // user, group or role already exists
        [15025] = SqlErrorKind.AlreadyExists, // server principal already exists

        [208] = SqlErrorKind.NotFound,  // invalid object name
        [911] = SqlErrorKind.NotFound,  // database does not exist
        [3701] = SqlErrorKind.NotFound, // cannot drop: does not exist (or no permission)
        [15007] = SqlErrorKind.NotFound, // not a valid login
        [15151] = SqlErrorKind.NotFound, // cannot find the principal (or no permission)

        [976] = SqlErrorKind.AgUnavailable,   // database in an AG, not accessible for queries
        [978] = SqlErrorKind.AgUnavailable,   // accessible with read-only intent only
        [983] = SqlErrorKind.AgUnavailable,   // availability database not accessible in this role
        [35250] = SqlErrorKind.AgUnavailable, // connection to the primary replica is not active

        [20] = SqlErrorKind.Transient,    // transport, on handshake
        [64] = SqlErrorKind.Transient,    // connection terminated
        [233] = SqlErrorKind.Transient,   // no process on the other end of the pipe
        [4060] = SqlErrorKind.Transient,  // cannot open database (login race during recovery)
        [10053] = SqlErrorKind.Transient, // connection aborted
        [10054] = SqlErrorKind.Transient, // connection forcibly closed
        [10060] = SqlErrorKind.Transient, // network timeout
        [10928] = SqlErrorKind.Transient, // resource limit reached
        [10929] = SqlErrorKind.Transient, // server busy
        [40143] = SqlErrorKind.Transient,
        [40197] = SqlErrorKind.Transient, // failover in progress
        [40501] = SqlErrorKind.Transient, // service busy
        [40613] = SqlErrorKind.Transient, // database currently unavailable
        [49918] = SqlErrorKind.Transient,
        [49919] = SqlErrorKind.Transient,
        [49920] = SqlErrorKind.Transient,
    };

    /// <summary>The kind of the first error of <paramref name="ex"/> that has one.</summary>
    public static SqlErrorKind Classify(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
            if (Kinds.TryGetValue(error.Number, out var kind))
                return kind;
        return Kinds.GetValueOrDefault(ex.Number, SqlErrorKind.Other);
    }

    public static SqlErrorKind Classify(int number) => Kinds.GetValueOrDefault(number, SqlErrorKind.Other);

    /// <summary>Worth retrying as is: transport and availability blips, deadlock victims, timeouts.</summary>
    public static bool IsTransient(SqlErrorKind kind)
        => kind is SqlErrorKind.Transient or SqlErrorKind.Deadlock or SqlErrorKind.CommandTimeout;
}
