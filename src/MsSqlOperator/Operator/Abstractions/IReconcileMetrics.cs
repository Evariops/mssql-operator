namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Optional hook for the runtime to report reconcile outcomes. Kept in the operator framework
/// namespace so the runtime stays decoupled from the application's concrete metrics backend;
/// the app registers an implementation (see OperatorMetrics) and the runtime resolves it if present.
/// </summary>
public interface IReconcileMetrics
{
    void RecordReconciliation(string controller, double durationSeconds, bool success);

    /// <summary>
    /// Registers the work-queue saturation probe of a kind: how many resources are
    /// waiting and how long the oldest one has been waiting. Without these, a saturated operator was
    /// invisible — /readyz stayed green while availability groups went unchecked for minutes, which is
    /// precisely the window in which a dead primary is not detected. The probe is read at collection
    /// time, so the gauge stays truthful when every worker is stuck.
    /// </summary>
    void RegisterQueue(string kind, Func<(int Depth, double OldestAgeSeconds)> probe);
}
