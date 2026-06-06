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
    IKubernetes client,
    OperatorOptions options,
    ILogger<ReconcilerHostedService> logger) : BackgroundService
{
    private readonly IReadOnlyList<IEntityRuntime> _runtimes = runtimes.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Operator runtime starting with {Count} entity runtimes: {Kinds}",
            _runtimes.Count, string.Join(", ", _runtimes.Select(r => r.EntityKind)));

        if (!options.LeaderElection)
        {
            await RunAllRuntimes(stoppingToken).ConfigureAwait(false);
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

        while (!stoppingToken.IsCancellationRequested)
        {
            using var leaderScope = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var elector = new LeaderElector(config);

            Task runners = Task.CompletedTask;
            elector.OnStartedLeading += () =>
            {
                logger.LogInformation("Acquired leadership ({Identity}); starting reconcilers", options.Identity);
                runners = RunAllRuntimes(leaderScope.Token);
            };
            elector.OnStoppedLeading += () =>
            {
                logger.LogWarning("Lost leadership ({Identity}); stopping reconcilers", options.Identity);
                leaderScope.Cancel();
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
                logger.LogError(e, "Leader election loop error; retrying");
            }
            finally
            {
                leaderScope.Cancel();
                try { await runners.ConfigureAwait(false); } catch { /* runtimes cancelled */ }
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(options.RetryPeriod, stoppingToken).ConfigureAwait(false);
        }
    }

    private Task RunAllRuntimes(CancellationToken ct)
        => Task.WhenAll(_runtimes.Select(r => r.RunAsync(ct)));
}
