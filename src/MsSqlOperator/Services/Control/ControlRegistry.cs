using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator;
using MsSqlOperator.Operator.Client;

namespace MsSqlOperator.Services.Control;

/// <summary>Another operation is in progress on the resource.</summary>
public sealed class IntentConflictException(string active, string requested)
    : InvalidOperationException($"operation {active} is in progress: {requested} is refused until it completes or is aborted");

/// <summary>
/// The control registry. Every write is a compare-and-swap (a JSON patch tested on the
/// resourceVersion) that carries the operator's current epoch — refused by the API server when a
/// newer leader wrote in between (CEL) — and is refused here when another intent is active. Nothing
/// is written in steady state: only at the transitions of an operation.
/// </summary>
public sealed class ControlRegistry(IKubernetesClient client, IOperatorEpoch epoch, TimeProvider? clock = null,
    ILogger<ControlRegistry>? logger = null)
{
    private TimeProvider Clock => clock ?? TimeProvider.System;

    public Task<SqlControlRecord?> GetAsync(string name, string ns, CancellationToken ct) => client.GetAsync<SqlControlRecord>(name, ns, ct);

    /// <summary>The record of <paramref name="owner"/>, created when missing (observation mode:
    /// <paramref name="reconstruct"/> fills it from what SQL and the status say, and the creation is
    /// stamped <c>reconstructedAt</c>).</summary>
    public async Task<SqlControlRecord> EnsureAsync(IKubernetesObject<V1ObjectMeta> owner, string name,
        Action<SqlControlRecordSpec>? reconstruct, CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty;
        if (await client.GetAsync<SqlControlRecord>(name, ns, ct) is { } existing)
            return existing;

        var spec = new SqlControlRecordSpec
        {
            Owner = new ControlOwner { Kind = owner.Kind, Name = owner.Metadata.Name, Uid = owner.Metadata.Uid },
            Epoch = epoch.Current,
        };
        if (reconstruct is not null)
        {
            reconstruct(spec);
            spec.ReconstructedAt = Clock.GetUtcNow().UtcDateTime;
        }
        var record = new SqlControlRecord
        {
            ApiVersion = "mssql-operator.evariops.com/v1alpha1",
            Kind = "SqlControlRecord",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                // No ownerReference: the record outlives the resource (GitOps recreation).
                Labels = new Dictionary<string, string>
                {
                    [OperatorLabels.ManagedBy] = OperatorLabels.ManagedByValue,
                    [OperatorLabels.OwnerKind] = owner.Kind,
                },
            },
            Spec = spec,
        };
        logger?.LogInformation("Created control record {Ns}/{Name}{Reconstructed}", ns, name,
            reconstruct is null ? "" : " (reconstructed)");
        return await client.CreateAsync(record, ct);
    }

    /// <summary>Starts an operation. Refused when another one is active (or by the API server when a
    /// newer epoch wrote the record).</summary>
    public Task<SqlControlRecord> BeginAsync(SqlControlRecord record, ControlIntent intent, CancellationToken ct)
    {
        if (record.Spec.Intent is { } active && active.Id != intent.Id)
            throw new IntentConflictException(active.Id, intent.Id);
        intent.Epoch = epoch.Current;
        intent.StartedAt ??= Clock.GetUtcNow().UtcDateTime;
        return WriteAsync(record, spec => spec.Intent = intent, ct);
    }

    /// <summary>Moves the operation in progress to <paramref name="stage"/>.</summary>
    public Task<SqlControlRecord> AdvanceAsync(SqlControlRecord record, string id, string stage, Action<ControlIntent>? update, CancellationToken ct)
    {
        if (record.Spec.Intent is not { } active || active.Id != id)
            throw new IntentConflictException(record.Spec.Intent?.Id ?? "(none)", id);
        return WriteAsync(record, spec =>
        {
            spec.Intent!.Stage = stage;
            update?.Invoke(spec.Intent);
        }, ct);
    }

    /// <summary>Ends the operation in progress — completed or aborted alike, the slot is free.</summary>
    public Task<SqlControlRecord> CompleteAsync(SqlControlRecord record, string id, Action<SqlControlRecordSpec>? update, CancellationToken ct)
    {
        if (record.Spec.Intent is { } active && active.Id != id)
            throw new IntentConflictException(active.Id, id);
        return WriteAsync(record, spec =>
        {
            spec.Intent = null;
            update?.Invoke(spec);
        }, ct);
    }

    public Task<SqlControlRecord> AbortAsync(SqlControlRecord record, string id, CancellationToken ct) => CompleteAsync(record, id, null, ct);

    /// <summary>Any other change (divergences, removed replicas, keys), under the same rules.</summary>
    public Task<SqlControlRecord> UpdateAsync(SqlControlRecord record, Action<SqlControlRecordSpec> update, CancellationToken ct)
        => WriteAsync(record, update, ct);

    private async Task<SqlControlRecord> WriteAsync(SqlControlRecord record, Action<SqlControlRecordSpec> update, CancellationToken ct)
    {
        var spec = k8s.KubernetesJson.Deserialize<SqlControlRecordSpec>(k8s.KubernetesJson.Serialize(record.Spec));
        update(spec);
        spec.Epoch = Math.Max(spec.Epoch, epoch.Current);
        Bound(spec);
        var written = await client.PatchAsync(record, [JsonPatch.Replace("/spec", spec)], ct);
        record.Spec = spec;
        return written;
    }

    /// <summary>The bounded lists: the oldest entries go first.</summary>
    internal static void Bound(SqlControlRecordSpec spec)
    {
        if (spec.OpenDivergences is { Count: > 16 } divergences)
            spec.OpenDivergences = divergences.OrderBy(d => d.At).TakeLast(16).ToList();
        if (spec.RemovedReplicas is { Count: > 8 } removed)
            spec.RemovedReplicas = removed.OrderBy(r => r.At).TakeLast(8).ToList();
        if (spec.Intent?.Isolated is { Count: > 8 } isolated)
            spec.Intent.Isolated = isolated.Take(8).ToList();
    }

    /// <summary>Deletes the records whose resource and volumes are gone.</summary>
    public async Task<int> SweepAsync(Func<SqlControlRecord, CancellationToken, Task<bool>> stillNeeded, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var record in await client.ListAsync<SqlControlRecord>(null, null, ct))
        {
            if (record.Spec.Intent is not null || await stillNeeded(record, ct))
                continue;
            await client.DeleteAsync(record, ct);
            deleted++;
            logger?.LogInformation("Deleted control record {Ns}/{Name}: its resource and its volumes are gone",
                record.Metadata.NamespaceProperty, record.Metadata.Name);
        }
        return deleted;
    }
}
