using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Services.SqlServer;

public interface IDatabaseMailManager
{
    Task EnableDatabaseMailAsync(SqlConnection connection, CancellationToken ct = default);
    Task CreateMailProfileAsync(SqlConnection connection, string profileName, string? description, bool isDefault, CancellationToken ct = default);
    Task CreateOrUpdateMailAccountAsync(SqlConnection connection, string accountName, string? description, string emailAddress, string? displayName, string? replyTo, string smtpServer, int port, bool useSsl, string? username, string? password, CancellationToken ct = default);
    Task AssociateAccountToProfileAsync(SqlConnection connection, string profileName, string accountName, CancellationToken ct = default);
    Task CreateOrUpdateOperatorAsync(SqlConnection connection, string operatorName, bool enabled, string? emailAddress, string? pagerAddress, string? weekdayPagerStart, string? weekdayPagerEnd, string? saturdayPagerStart, string? saturdayPagerEnd, CancellationToken ct = default);
    Task ConfigureAsync(SqlConnection connection, DatabaseMailSpec spec, Func<SecretReference, CancellationToken, Task<string?>>? resolveSecret = null, CancellationToken ct = default);
    Task ConfigureOperatorsAsync(SqlConnection connection, List<AgentOperatorSpec> operators, CancellationToken ct = default);
}
