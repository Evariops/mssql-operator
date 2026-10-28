using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Services.Metrics;

/// <summary>
/// One trace per operation — failover, fencing, re-seed, rotation, deployment — and one
/// span per step. The operation id (the trace id) goes into the log scope, so that every line an
/// operation writes, across classes, can be found with it.
/// </summary>
public static class OperatorTracing
{
    public const string SourceName = "MsSqlOperator";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    /// <summary>Starts an operation (a root span when none is current) and opens its log scope.</summary>
    public static Operation Start(ILogger logger, string operation, string resource, params (string Key, object? Value)[] tags)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("mssqloperator.resource", resource);
        foreach (var (key, value) in tags)
            activity?.SetTag(key, value);
        var operationId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var scope = logger.BeginScope(new Dictionary<string, object> { ["operationId"] = operationId, ["operation"] = operation });
        return new Operation(activity, scope, operationId);
    }

    /// <summary>A step of the current operation.</summary>
    public static Activity? Step(string name) => Source.StartActivity(name, ActivityKind.Internal);

    public sealed class Operation(Activity? activity, IDisposable? scope, string id) : IDisposable
    {
        public string Id { get; } = id;

        public void Fail(Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
        }

        public void Dispose()
        {
            scope?.Dispose();
            activity?.Dispose();
        }
    }
}
