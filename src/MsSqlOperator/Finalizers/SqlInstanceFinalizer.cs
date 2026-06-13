using MsSqlOperator.Operator.Abstractions;
using MsSqlOperator.Operator.Client;
using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Finalizers;

public class SqlInstanceFinalizer(
    ILogger<SqlInstanceFinalizer> logger) : IEntityFinalizer<SqlInstance>
{
    public string Identifier => "mssql-operator.evariops.com/sqlinstance-finalizer";

    /// <summary>Type-name-derived identifier emitted by pre-F4 operator versions.</summary>
    public IReadOnlyList<string> LegacyIdentifiers => ["sqlinstancefinalizer.finalizers.mssql-operator.evariops.com"];

    public Task<ReconciliationResult<SqlInstance>> FinalizeAsync(SqlInstance entity, CancellationToken ct)
    {
        logger.LogInformation("Finalizing SqlInstance {Name}", entity.Metadata.Name);
        // StatefulSet, Services, PVCs are cleaned up via owner references
        logger.LogInformation("SqlInstance {Name} finalized", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<SqlInstance>.Success(entity));
    }
}
