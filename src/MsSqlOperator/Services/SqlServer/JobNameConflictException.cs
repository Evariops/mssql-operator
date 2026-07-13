namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Raised when an msdb SQL Agent job of the same name exists but was not created by the operator
/// Adopting it would silently rewrite a DBA's steps and schedules, and the finalizer
/// would DROP it when the CR is deleted.
/// </summary>
public class JobNameConflictException(string message) : InvalidOperationException(message);
