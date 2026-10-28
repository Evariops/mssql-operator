namespace MsSqlOperator.Services.ConnectionFactory;

/// <summary>
/// How a connection behaves, chosen by what it is for. A health probe must fail in seconds; a
/// DDL statement must not queue behind an application lock — and hold every caller of the object
/// behind it — for the 30 s default; an HADR command must not wait on a lock for ever either.
/// </summary>
public enum SqlProfile
{
    /// <summary>The historical behaviour: SqlConnection:ConnectTimeout, OpenRetryCount transient
    /// retries, 30 s commands, no session settings.</summary>
    Default,

    /// <summary>Health and state reads: 3 s to connect, no retry, 5 s commands.</summary>
    Probe,

    /// <summary>DDL (procedures, logins, users, jobs): 5 s to connect, 2 retries on transient errors,
    /// 30 s commands; LOCK_TIMEOUT 3000, DEADLOCK_PRIORITY LOW, XACT_ABORT ON.</summary>
    Ddl,

    /// <summary>HADR commands: 3 s to connect, 1 retry, 30 s commands; LOCK_TIMEOUT 5000, XACT_ABORT ON.</summary>
    Hadr,

    /// <summary>Backups, seeding, encryption scans: 5 s to connect, 1 retry, LOCK_TIMEOUT 5000; the
    /// long command sets its own timeout, the others keep 30 s.</summary>
    LongRunning,

    /// <summary>DDL on every server of a target at once — 3 s to connect and no retry, so a
    /// dead replica costs seconds (it is retried with the next pass); then the DDL commands and session
    /// settings of <see cref="Ddl"/>.</summary>
    DdlFanout,
}

/// <summary>The settings of a <see cref="SqlProfile"/>. <see cref="SessionBatch"/> is sent in its own
/// batch right after the connection opens, and repeated at the head of DDL batches: a pooled
/// connection does not always come back with the session settings reset.</summary>
public sealed record SqlProfileSettings(int ConnectTimeoutSeconds, int OpenRetries, int CommandTimeoutSeconds, string? SessionBatch)
{
    public static SqlProfileSettings For(SqlProfile profile, SqlConnectionOptions options) => profile switch
    {
        SqlProfile.Probe => new(3, 0, 5, null),
        SqlProfile.Ddl => new(5, 2, 30, DdlSession),
        SqlProfile.Hadr => new(3, 1, 30, "SET LOCK_TIMEOUT 5000; SET XACT_ABORT ON;"),
        SqlProfile.LongRunning => new(5, 1, 30, "SET LOCK_TIMEOUT 5000;"),
        SqlProfile.DdlFanout => new(3, 0, 30, DdlSession),
        _ => new(options.ConnectTimeout, options.OpenRetryCount, 30, null),
    };

    /// <summary>The session settings of a DDL statement: wait 3 s at most for a lock, lose a
    /// deadlock rather than win it against the application, roll everything back on any error.</summary>
    public const string DdlSession = "SET LOCK_TIMEOUT 3000; SET DEADLOCK_PRIORITY LOW; SET XACT_ABORT ON;";
}
