using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Core.Safety;
using MsSqlOperator.Core.State;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Services.Health;

/// <summary>
/// One read-only state read of an AG on its primary — replicas, databases, k — with the
/// short command timeout of the probe profile. A read that fails is itself a signal.
/// </summary>
public sealed class AgStateReader(ISqlConnectionFactory connectionFactory, ISqlAgManager agManager)
{
    public async Task<AgSnapshot> ReadAsync(string ag, string ns, string primary, string saPassword, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenAsync($"{primary}.{ag}-headless.{ns}.svc.cluster.local",
            SqlEndpoints.ContainerPort, saPassword, SqlProfile.Probe, "master", ct);
        var replicas = await agManager.GetReplicaStatesAsync(conn, ag, ct);
        var databases = await agManager.GetDatabaseStatesAsync(conn, ag, ct);
        var k = await agManager.GetRequiredSyncSecondariesAsync(conn, ag, ct);
        var actualPrimary = replicas.FirstOrDefault(r => r.Role == "PRIMARY")?.Name;
        return new AgSnapshot(ag, at, actualPrimary, k ?? 0,
            replicas.Select(r => new ReplicaState(r.Name, r.Role, r.ConnectionState == "CONNECTED", r.SynchronizationHealth, r.AvailabilityMode)).ToList(),
            databases.Select(d => new DatabaseState(d.DatabaseName, d.ReplicaName, d.SynchronizationState, d.IsSuspended)).ToList());
    }
}

/// <summary>
/// The event-driven supervisor of the availability groups, at safety level 3. One actor per
/// AG: a mailbox that coalesces a burst of signals into one re-evaluation, a state read
/// every H, a safety tick, a global budget of concurrent evaluations, and a heartbeat on the
/// liveness probe. In Shadow its reads are compared with the AG controller's health check
/// and every gap is logged; in Enforce they feed the state store, whose transitions wake the AGDs.
/// Off below level 3 (the default ceiling is 1).
/// </summary>
public sealed class AgSupervisor(IKubernetesClient client, AgStateReader reader, IAgStateStore store,
    Operator.Registration.OperatorOptions options, Metrics.OperatorMetrics? metrics, Operator.Runtime.RuntimeLiveness? liveness,
    ILogger<AgSupervisor> logger, TimeProvider? clock = null, IEnumerable<Operator.Runtime.IEntityRuntime>? runtimes = null) : BackgroundService
{
    public static readonly TimeSpan StateReadPeriod = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SafetyTick = TimeSpan.FromSeconds(60);
    private const int MaxConcurrentEvaluations = 16;

    private readonly ConcurrentDictionary<(string Ns, string Ag), Actor> _actors = new();
    private readonly SemaphoreSlim _budget = new(MaxConcurrentEvaluations);
    private CancellationToken _stopping;
    private TimeProvider Clock => clock ?? TimeProvider.System;

    private sealed class Actor
    {
        public Channel<string> Mailbox { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public DateTimeOffset LastActivity { get; set; }
        public Task? Loop { get; set; }
    }

    public SafetyMode? Mode(SqlAvailabilityGroup ag)
        => SafetyLevel.Mode(3, SafetyLevel.Effective(ag.Metadata.Annotations?.TryGetValue(SafetyLevel.Annotation, out var l) == true ? l : null,
            options.SafetyLevelMax), options.SafetyLevelMax);

    /// <summary>A signal for the actor of the AG — a reconcile, a pod event, a transition.
    /// Creates the actor on first use.</summary>
    public void Signal(SqlAvailabilityGroup ag, string trigger)
    {
        if (Mode(ag) is null || _stopping.IsCancellationRequested)
            return;
        var key = (ag.Metadata.NamespaceProperty, ag.Metadata.Name);
        var actor = _actors.GetOrAdd(key, k =>
        {
            var created = new Actor { LastActivity = Clock.GetUtcNow() };
            liveness?.Register($"AgSupervisor/{k.Item1}/{k.Item2}", () => (Clock.GetUtcNow() - created.LastActivity).TotalSeconds, 3 * SafetyTick);
            created.Loop = Task.Run(() => RunActorAsync(k, created, _stopping));
            return created;
        });
        actor.Mailbox.Writer.TryWrite(trigger);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task RunActorAsync((string Ns, string Ag) key, Actor actor, CancellationToken ct)
    {
        var lastTick = Clock.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            // Wait for a signal or the state read period, whichever comes first; then coalesce.
            var drained = new List<string>();
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(StateReadPeriod);
                try { drained.Add(await actor.Mailbox.Reader.ReadAsync(wait.Token)); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            while (actor.Mailbox.Reader.TryRead(out var more)) drained.Add(more);
            // The decision is the core's; the loop only waits and applies.
            var next = Core.Supervision.SupervisorLogic.Next(drained, Clock.GetUtcNow(), lastTick, SafetyTick);
            lastTick = next.TickAt;
            var (trigger, coalesced) = (next.Trigger, next.Coalesced);
            metrics?.ActorReevaluated("AgSupervisor", trigger);

            await _budget.WaitAsync(ct);
            try
            {
                if (!await EvaluateAsync(key, ct))
                {
                    _actors.TryRemove(key, out _); // the AG is gone, or left level 3
                    return;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AG supervisor of {Ns}/{Ag}: evaluation failed ({Coalesced} signals coalesced)", key.Ns, key.Ag, coalesced);
            }
            finally
            {
                _budget.Release();
                actor.LastActivity = Clock.GetUtcNow();
            }
        }
    }

    /// <summary>One re-evaluation: the state read, compared (Shadow) or stored (Enforce). False when
    /// the actor must stop.</summary>
    private async Task<bool> EvaluateAsync((string Ns, string Ag) key, CancellationToken ct)
    {
        var ag = await client.GetAsync<SqlAvailabilityGroup>(key.Ag, key.Ns, ct);
        if (ag is null || Mode(ag) is not { } mode)
            return false;
        var primary = store.FreshPrimary(key.Ns, key.Ag) ?? ag.Status?.PrimaryReplica;
        var saPassword = await SecretValues.TryGetAsync(client, ag.Spec.SaPasswordSecretRef, key.Ns, ct);
        if (primary is null || saPassword is null)
            return true;

        var watch = Stopwatch.StartNew();
        AgSnapshot? snapshot = null;
        try
        {
            snapshot = await reader.ReadAsync(key.Ag, key.Ns, primary, saPassword, Clock.GetUtcNow(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The read failed — itself a signal; the secondaries are probed by the next
            // health pass and the election shell (their watchdog states feed the preconditions).
            logger.LogInformation(ex, "AG {Ag}: state read on {Primary} failed", key.Ag, primary);
        }
        metrics?.AgHeartbeat(key.Ns, key.Ag, watch.Elapsed.TotalSeconds, snapshot is not null);

        if (mode == SafetyMode.Enforce)
        {
            // The transitions wake the databases of the AG, as the controller's read does.
            await AgdWakeup.WakeAsync(client, runtimes, key.Ns, key.Ag, store.Update(key.Ns, key.Ag, snapshot), ct);
            return true;
        }
        // Shadow: compared with the controller's last read, never acted upon.
        var reference = store.Get(key.Ns, key.Ag);
        var gaps = Core.Supervision.SupervisorLogic.ShadowGaps(reference, snapshot);
        if (gaps.Count > 0)
            logger.LogInformation("Level 3 shadow on AG {Ag}: supervisor and health check differ: {Gaps}", key.Ag,
                string.Join("; ", gaps.Select(t => $"{t.Kind} {t.Subject} {t.From}→{t.To}")));
        return true;
    }

    public override void Dispose()
    {
        _budget.Dispose();
        base.Dispose();
    }
}
