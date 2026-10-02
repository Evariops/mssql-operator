namespace MsSqlOperator.Services.Kubernetes;

/// <summary>
/// Raised when a StatefulSet deleted with propagationPolicy=Orphan (storage expansion, selector
/// migration — <see cref="StatefulSetRecreation"/>) is still present after the bounded wait. Re-applying on top of the terminating object
/// fails with a 422 and restarts the whole patch/delete cycle on the next backoff; requeueing is the
/// correct answer.
/// </summary>
public class StatefulSetNameNotReleasedException(string message) : InvalidOperationException(message);
