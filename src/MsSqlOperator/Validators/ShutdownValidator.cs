using MsSqlOperator.Entities.Common;

namespace MsSqlOperator.Validators;

/// <summary>
/// <c>terminationGracePeriodSeconds</c> is what stands between SQL Server and a SIGKILL
/// in the middle of a checkpoint. The schema accepted 0, which turns every planned shutdown — a
/// rolling update, a node drain, a failover — into a crash: the next startup replays recovery on
/// every database, and (with full-text) resumes the interrupted population crawls on top of it.
/// </summary>
public static class ShutdownValidator
{
    /// <summary>Below this, a busy SQL Server cannot be counted on to checkpoint and shut down.</summary>
    public const int MinimumGracePeriodSeconds = 60;

    public static string? Validate(ShutdownSpec? shutdown)
    {
        if (shutdown?.GracePeriodSeconds is not { } grace)
            return null;

        if (grace < MinimumGracePeriodSeconds)
            return $"shutdown.gracePeriodSeconds must be at least {MinimumGracePeriodSeconds}: a shorter period SIGKILLs " +
                   "sqlservr mid-checkpoint, so every restart replays a crash recovery (and, with full-text, resumes the " +
                   "interrupted population crawls at boot).";

        if (grace > 3600)
            return "shutdown.gracePeriodSeconds must not exceed 3600: a node drain would block for an hour.";

        return null;
    }
}
