using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Operator.Runtime;

namespace MsSqlOperator.Operator.Registration;

/// <summary>
/// Hosts the per-entity reconciliation runtimes, replacing KubeOps' operator host. When leader
/// election is enabled, the watchers run only while this replica holds the Lease; admission
/// webhooks (served by ASP.NET) are unaffected and always respond.
/// </summary>
public sealed class ReconcilerHostedService(
    IEnumerable<IEntityRuntime> runtimes,
    OwnedResourceWatcher ownedResourceWatcher,
    IKubernetes client,
    OperatorOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ReconcilerHostedService> logger,
    MsSqlOperator.Operator.Abstractions.IReconcileMetrics? reconcileMetricsService = null,
    MsSqlOperator.Services.Control.IOperatorEpoch? operatorEpoch = null) : BackgroundService
{
    private readonly IReadOnlyList<IEntityRuntime> _runtimes = runtimes.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operator runtime starting with {Count} entity runtimes: {Kinds}",
            _runtimes.Count, string.Join(", ", _runtimes.Select(r => r.EntityKind)));

        var reconcileMetrics = reconcileMetricsService;
        if (!options.LeaderElection)
        {
            reconcileMetrics?.SetLeader(true);
            await SuperviseAsync(RunAllRuntimes(stoppingToken), stoppingToken).ConfigureAwait(false);
            return;
        }

        var ns = options.ResolveNamespace();
        var leaseLock = new LeaseLock(client, ns, options.LeaseName, options.Identity);
        var config = new LeaderElectionConfig(leaseLock)
        {
            LeaseDuration = options.LeaseDuration,
            RenewDeadline = options.RenewDeadline,
            RetryPeriod = options.RetryPeriod,
        };

        // A process that loses the Lease never re-elects in-process. Another replica
        // may already be reconciling; the only safe state for this process is gone. Kubernetes
        // restarts the pod, which campaigns again with a clean slate (controller-runtime
        // semantics). The residual window between the lease actually expiring on the API
        // server and OnStoppedLeading firing here cannot be closed without storage fencing —
        // other safeguards reduce the blast radius of that window.
        using var leaderScope = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var elector = new LeaderElector(config);

        // Completed exactly once from the elector callback thread; replaces the racy
        // local-variable handoff (written in OnStartedLeading, read in finally).
        var runnersStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        elector.OnStartedLeading += () =>
        {
            logger.LogInformation("Acquired leadership ({Identity}); starting reconcilers", options.Identity);
            reconcileMetrics?.SetLeader(true);
            // A new epoch, above every one seen, before anything is reconciled.
            var runners = StartLeadingAsync(leaderScope.Token);
            _ = SuperviseAsync(runners, leaderScope.Token);
            runnersStarted.TrySetResult(runners);
        };
        elector.OnStoppedLeading += () =>
        {
            reconcileMetrics?.SetLeader(false);
            leaderScope.Cancel();
            if (stoppingToken.IsCancellationRequested)
                return; // normal shutdown, not a leadership loss

            logger.LogCritical(
                "Lost leadership ({Identity}); terminating the process — another writer may be active. Kubernetes will restart the pod.",
                options.Identity);
            lifetime.StopApplication();
            _ = Task.Delay(OperatorOptions.LostLeadershipExitDeadline).ContinueWith(
                _ =>
                {
                    logger.LogCritical("Graceful shutdown exceeded {Deadline}s after leadership loss; exiting hard", OperatorOptions.LostLeadershipExitDeadline.TotalSeconds);
                    Environment.Exit(1);
                },
                TaskScheduler.Default);
        };

        try
        {
            await elector.RunUntilLeadershipLostAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception e)
        {
            // Election machinery failed (e.g. API server unreachable before any acquisition):
            // same answer — stop the process, let Kubernetes restart it.
            logger.LogCritical(e, "Leader election failed; terminating the process");
            lifetime.StopApplication();
        }
        finally
        {
            leaderScope.Cancel();
            if (runnersStarted.Task is { IsCompletedSuccessfully: true } started)
            {
                try { await (await started.ConfigureAwait(false)).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* runtimes cancelled */ }
                catch (Exception e) { logger.LogError(e, "A reconciliation runtime ended with an error"); }
            }
        }
    }

    /// <summary>
    /// The runtimes must run for as long as the process leads. One that ends or fails on
    /// its own — an exception escaping a worker loop, a watch that gave up — left a leader holding
    /// the Lease and reconciling nothing; no other replica could take over. The process stops
    /// instead, and Kubernetes restarts it.
    /// </summary>
    private async Task SuperviseAsync(Task runners, CancellationToken ct)
    {
        try
        {
            await runners.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "A reconciliation runtime failed; stopping the process so that Kubernetes restarts it");
            lifetime.StopApplication();
            return;
        }
        if (!ct.IsCancellationRequested)
        {
            logger.LogCritical("The reconciliation runtimes ended on their own; stopping the process so that Kubernetes restarts it");
            lifetime.StopApplication();
        }
    }

    private async Task StartLeadingAsync(CancellationToken ct)
    {
        if (operatorEpoch is not null)
            await operatorEpoch.AdvanceAsync(ct).ConfigureAwait(false);
        await RunAllRuntimes(ct).ConfigureAwait(false);
    }

    private Task RunAllRuntimes(CancellationToken ct)
        // The owned-resource watch joins the runtimes under the leader guard, so it stops
        // with them on a leadership loss and never runs on a non-leader replica.
        => Task.WhenAll(_runtimes.Select(r => r.RunAsync(ct)).Append(ownedResourceWatcher.RunAsync(ct)));
}
