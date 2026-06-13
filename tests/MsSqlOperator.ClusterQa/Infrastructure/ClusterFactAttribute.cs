using System.Runtime.CompilerServices;

namespace MsSqlOperator.ClusterQa.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips unless the cluster-QA gate is on
/// (<c>QA_CLUSTER=1</c>). This is what keeps the suite out of normal CI: even if something ran
/// `dotnet test` on this project, every scenario would report Skipped rather than fail.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClusterFactAttribute : FactAttribute
{
    public ClusterFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!QaConfig.Enabled)
            Skip = "Cluster QA disabled. Set QA_CLUSTER=1 (and QA_KUBE_CONTEXT) to run against a live cluster.";
    }
}
