using k8s.Models;
using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Services.Kubernetes;

namespace MsSqlOperator.Finalizers;

public class SqlUserFinalizer(
    ILogger<SqlUserFinalizer> logger,
    IKubernetesClient kubernetesClient,
    ISqlConnectionFactory connectionFactory,
    ISqlUserManager sqlUserManager,
    ISqlServerManager sqlServerManager,
    ITargetResolver targetResolver,
    IEventPublisher eventPublisher,
    MsSqlOperator.Services.Actuation.ActuationGateway? gateway = null) : IEntityFinalizer<SqlUser>
{
    /// <summary>The way to a disruptive or destructive action.</summary>
    private MsSqlOperator.Services.Actuation.ActuationGateway Gateway => gateway ?? MsSqlOperator.Services.Actuation.ActuationGateway.Default;

    public string Identifier => "mssql-operator.evariops.com/sqluser-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqluserfinalizer.finalizers.mssql-operator.evariops.com"];

    private const int MaxAttempts = 3;

    public async Task<ReconciliationResult<SqlUser>> FinalizeAsync(SqlUser entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlUser {Name}", entity.Metadata.Name);

        var login = entity.Spec.Login;
        if (login == null) return ReconciliationResult<SqlUser>.Success(entity);

        var forceFinalize = entity.Metadata.Annotations?.TryGetValue("mssql-operator.evariops.com/force-finalize", out var forceVal) == true
                            && forceVal == "true";

        // Never DROP a login the operator did not create. status.appliedLoginName is only
        // stamped once this CR provisioned the login; without it, the login in SQL Server merely shares
        // the name — a DBA's 'reporting' login, say — and dropping it (plus its database users, with a
        // forced schema transfer to dbo) is an immediate production outage. This is the exact failure of
        // a CR that never converged (bad secretRef) and was deleted to be re-created.
        if (!string.Equals(entity.Status?.AppliedLoginName, login.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "SqlUser {Name}: login '{Login}' was never provisioned by this resource (status.appliedLoginName is not set); " +
                "leaving it untouched", entity.Metadata.Name, login.Name);
            await eventPublisher.PublishAsync(entity, "CleanupSkippedNotOwned",
                $"SQL cleanup skipped: login '{login.Name}' was not provisioned by this SqlUser, so it is not the operator's to drop.",
                ct: ct);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        var ns = entity.Metadata.NamespaceProperty;
        var resolvedTargets = await targetResolver.ResolveAsync(entity.Spec.Target, ns, ct);
        entity.Status ??= new SqlUserStatus();
        var pending = new List<(ResolvedTarget Target, List<(string Server, string Error)> Servers)>();

        foreach (var target in resolvedTargets)
        {
            // Each server on its own, the login disabled first. A server handled on an earlier
            // pass is not visited again; the finalizer completes once every server is handled.
            var outcome = await LoginRevocation.RevokeAsync(target, login.Name, drop: true, terminateSessions: true,
                connectionFactory, kubernetesClient, sqlUserManager, sqlServerManager, logger, ct, Gateway, entity,
                MsSqlOperator.Services.Actuation.ActuationIntent.ResourceDeletion);
            var done = entity.Status.RevokedServers ??= [];
            done.AddRange(outcome.Done.Except(done));
            var stillPending = outcome.Pending.Where(p => !done.Contains(p.Server)).ToList();
            if (stillPending.Count > 0)
                pending.Add((target, stillPending));
            else
                foreach (var server in outcome.Done)
                    logger.LogInformation("Login {Login} dropped on {Server}", login.Name, server);
        }

        if (pending.Count == 0)
            return ReconciliationResult<SqlUser>.Success(entity);
        if (forceFinalize)
        {
            await eventPublisher.PublishAsync(entity, "SqlArtifactsOrphaned",
                $"force-finalize: cleanup skipped on {string.Join(", ", pending.SelectMany(p => p.Servers).Select(s => s.Server))}; " +
                $"login '{login.Name}' may remain there.", "Warning", ct);
            return ReconciliationResult<SqlUser>.Success(entity);
        }

        // The attempt counter is persisted (the runtime re-fetches the object on every pass).
        // Past MaxAttempts the finalizer is kept and a human is asked — no silent success.
        var maxAttempts = 0;
        foreach (var (target, _) in pending)
        {
            var attempts = PreviousAttempts(entity, target.Name) + 1;
            (entity.Status.FinalizeAttempts ??= [])[target.Name] = attempts;
            maxAttempts = Math.Max(maxAttempts, attempts);
        }
        var servers = string.Join(", ", pending.SelectMany(p => p.Servers).Select(s => $"{s.Server} ({s.Error})"));
        if (maxAttempts >= MaxAttempts)
        {
            var message = $"Login '{login.Name}' could not be dropped on {servers} after {MaxAttempts} attempts: it stays active " +
                          "there. The finalizer is kept and retried; set the annotation mssql-operator.evariops.com/force-finalize=true " +
                          "to give up the cleanup explicitly.";
            await eventPublisher.PublishAsync(entity, "RevocationIncomplete", message, "Warning", ct);
            NeedsAttention.Require(entity.Status.Conditions ??= [], "RevocationIncomplete", message);
        }
        try
        {
            var updated = await kubernetesClient.UpdateStatusAsync(entity, ct);
            entity.Metadata.ResourceVersion = updated.Metadata.ResourceVersion;
        }
        catch (Exception persistEx) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(persistEx, "Could not persist the finalization progress of {Name}", entity.Metadata.Name);
        }
        return ReconciliationResult<SqlUser>.Failure(entity, $"Finalization pending on {servers}, attempt {maxAttempts}");
    }

    private static int PreviousAttempts(SqlUser entity, string target)
    {
        if (entity.Status?.FinalizeAttempts?.TryGetValue(target, out var recorded) == true)
            return recorded;
        return entity.Metadata.Annotations?.TryGetValue($"mssql-operator.evariops.com/finalize-attempts-{target}", out var legacy) == true
               && int.TryParse(legacy, out var parsed)
            ? parsed
            : 0;
    }

}
