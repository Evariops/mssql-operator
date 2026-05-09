namespace MsSqlOperator.Services.SqlServer;

public interface ISqlAgManager
{
    Task EnableHadrAsync(Microsoft.Data.SqlClient.SqlConnection connection, CancellationToken ct = default);
    Task CreateEndpointAsync(Microsoft.Data.SqlClient.SqlConnection connection, string endpointName, int port, CancellationToken ct = default);
    Task<bool> AvailabilityGroupExistsAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task CreateAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, List<string> databases, string seedingMode, CancellationToken ct = default);
    Task JoinAvailabilityGroupAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<string?> GetPrimaryReplicaAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task<List<(string name, string role, string syncState)>> GetReplicaStatesAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task FailoverAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, CancellationToken ct = default);
    Task SuspendDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task ResumeDataMovementAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task AddDatabaseToAgAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string database, CancellationToken ct = default);
    Task RemoveReplicaAsync(Microsoft.Data.SqlClient.SqlConnection connection, string agName, string replicaName, CancellationToken ct = default);
}
