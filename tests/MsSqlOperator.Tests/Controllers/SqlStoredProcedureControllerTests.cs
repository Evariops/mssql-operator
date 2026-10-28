using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Controllers;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.GitHub;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Controllers;

public class SqlStoredProcedureControllerTests
{
    private const string Definition = "CREATE PROCEDURE dbo.usp_orders AS SELECT 1";

    private readonly FakeKubernetesClient _k8s = new();
    private readonly FakeSqlStoredProcedureManager _procs = new();
    private readonly FakeSqlServerManager _servers = new() { DatabaseNames = ["AppDb"] };
    private readonly FakeTargetResolver _targets = new();
    private readonly SqlStoredProcedureController _controller;

    public SqlStoredProcedureControllerTests()
    {
        _targets.Resolved = [new(TargetKinds.Instance, "my-sql", "default", "my-sql.default.svc.cluster.local", 1433, "P@ss!", null)];
        _controller = new SqlStoredProcedureController(NullLogger<SqlStoredProcedureController>.Instance, _k8s,
            new FakeSqlConnectionFactory(), _procs, _servers, new DatabaseFilter(), new NoGitHub(), _targets,
            new FakeProcDeployerLoginManager(), new FakeEventPublisher(), new OperatorMetrics());
    }

    private static SqlStoredProcedure Procedures(string strategy) => new()
    {
        Metadata = new V1ObjectMeta { Name = "procs", NamespaceProperty = "default", Generation = 1 },
        Spec = new SqlStoredProcedureSpec
        {
            Target = new TargetSelector { Instances = ["my-sql"] },
            Databases = ["AppDb"],
            Inline = [new InlineProcedureSpec { Name = "usp_orders", Schema = "dbo", Definition = Definition }],
            DeploymentOptions = new DeploymentOptionsSpec { Strategy = strategy, Transactional = false, ValidateSyntax = false },
        },
    };

    [Theory]
    [InlineData("CreateOrAlter")]
    [InlineData("DropAndCreate")]
    public async Task ReconcileAsync_ProcedureUnchangedOnBothSides_NoDdl(string strategy)
    {
        // DropAndCreate dropped and recreated every procedure on every resync, losing the
        // permissions granted on them each time. The marker now decides, whatever the strategy.
        var hash = ProcedureScript.Parse(Definition).Single().Hash;
        _procs.States["AppDb"] = [new ProcedureDeploymentState("dbo", "usp_orders", ProcedureMarker.Prefix("default/procs", hash) + "ABCDEF", "abcdef")];

        await _controller.ReconcileAsync(Procedures(strategy), CancellationToken.None);
        await _controller.ReconcileAsync(Procedures(strategy), CancellationToken.None);

        Assert.Empty(_procs.DropCalls);
        Assert.Empty(_procs.DeployCalls);
    }

    [Fact]
    public async Task ReconcileAsync_DropAndCreate_OnlyTheChangedProcedureIsDroppedAndRecreated()
    {
        _procs.States["AppDb"] = [new ProcedureDeploymentState("dbo", "usp_orders", ProcedureMarker.Prefix("default/procs", "sha256:old") + "ABCDEF", "abcdef")];

        await _controller.ReconcileAsync(Procedures("DropAndCreate"), CancellationToken.None);

        Assert.Equal([("AppDb", "dbo", "usp_orders")], _procs.DropCalls);
    }

    private sealed class NoGitHub : IGitHubContentFetcher
    {
        public Task<string> ResolveTagToCommitShaAsync(string repository, string tag, string? token, CancellationToken ct = default)
            => throw new InvalidOperationException("no GitHub source here");
        public Task<string?> GetLatestReleaseTagAsync(string repository, string? releaseFilter, string? token, CancellationToken ct = default)
            => throw new InvalidOperationException("no GitHub source here");
        public Task<List<(string path, string content)>> FetchFilesAsync(GitHubSourceSpec source, string commitSha, string? token, CancellationToken ct = default)
            => throw new InvalidOperationException("no GitHub source here");
    }
}
