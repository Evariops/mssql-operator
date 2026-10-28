using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Control;

/// <summary>
/// A control record lives as long as its resource or its volumes do — it survives the
/// deletion and the recreation of the resource, but not the end of the data it protects. Every
/// 30 minutes the records whose resource and PVCs are both gone are deleted (idempotent: any replica
/// may run it).
/// </summary>
public sealed class ControlRecordSweeper(ControlRegistry registry, IKubernetesClient client, ILogger<ControlRecordSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await registry.SweepAsync(StillNeededAsync, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Control record sweep failed; next one in 30 minutes");
            }
        }
    }

    internal async Task<bool> StillNeededAsync(SqlControlRecord record, CancellationToken ct)
    {
        var ns = record.Metadata.NamespaceProperty;
        var owner = record.Spec.Owner;
        var exists = owner.Kind switch
        {
            "SqlAvailabilityGroup" => await client.GetAsync<SqlAvailabilityGroup>(owner.Name, ns, ct) is not null,
            "SqlInstance" => await client.GetAsync<SqlInstance>(owner.Name, ns, ct) is not null,
            _ => true,
        };
        if (exists)
            return true;
        var pvcs = await client.ListAsync<V1PersistentVolumeClaim>(ns, null, ct);
        // The claims of a StatefulSet are named <template>-<owner>-<ordinal>.
        return pvcs.Any(p => p.Metadata.Name.Contains($"-{owner.Name}-", StringComparison.Ordinal));
    }
}
