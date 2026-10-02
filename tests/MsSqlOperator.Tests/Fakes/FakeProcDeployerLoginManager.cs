using Microsoft.Data.SqlClient;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Hand-written fake for <see cref="IProcDeployerLoginManager"/>: returns a fixed deployer
/// password and records the (target, databases) it was asked to provision and release.</summary>
public sealed class FakeProcDeployerLoginManager : IProcDeployerLoginManager
{
    public string LoginName { get; set; } = "mssqlop_proc_deployer";
    public string Password { get; set; } = "deployerPwd!";
    public readonly List<(ResolvedTarget Target, IReadOnlyList<string> Databases)> EnsureCalls = [];
    public int Released { get; private set; }

    public Task<IProcDeployerLease> AcquireAsync(ResolvedTarget target, SqlConnection saConnection, IReadOnlyList<string> databases, CancellationToken ct)
    {
        EnsureCalls.Add((target, databases));
        return Task.FromResult<IProcDeployerLease>(new Lease(this));
    }

    private sealed class Lease(FakeProcDeployerLoginManager owner) : IProcDeployerLease
    {
        public string Password => owner.Password;

        public ValueTask DisposeAsync()
        {
            owner.Released++;
            return ValueTask.CompletedTask;
        }
    }
}
