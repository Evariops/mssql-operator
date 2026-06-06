namespace MsSqlOperator.Operator.Abstractions;

/// <summary>
/// Optional hook for the runtime to report reconcile outcomes. Kept in the operator framework
/// namespace so the runtime stays decoupled from the application's concrete metrics backend;
/// the app registers an implementation (see OperatorMetrics) and the runtime resolves it if present.
/// </summary>
public interface IReconcileMetrics
{
    void RecordReconciliation(string controller, double durationSeconds, bool success);
}
