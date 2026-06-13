using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Registration;
using MsSqlOperator.Operator.ViewApi;
using MsSqlOperator.Operator.Webhooks;
using MsSqlOperator.Services.Backup;
using MsSqlOperator.Services.Breakout;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.GitHub;
using MsSqlOperator.Services.Health;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Metrics;
using MsSqlOperator.Services.S3;
using MsSqlOperator.Services.SqlServer;
using OpenTelemetry.Metrics;
using Serilog;

// Code generation mode (replaces `kubeops generate`): `dotnet run -- generate <output-dir>`.
if (args.Length > 0 && args[0] == "generate")
    return await CodegenRunner.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// The aggregated view API authenticates the kube-aggregator by its front-proxy client certificate,
// which is issued by the cluster's requestheader CA — NOT by anything in the operator's trust store.
// Kestrel's default client-certificate validation would therefore reject it at the TLS layer (the
// aggregator sees "unexpected EOF" → FailedDiscoveryCheck). We accept any client cert at the TLS layer
// and defer the real trust decision (chain to requestheader-client-ca + allowed-names) to
// FrontProxyAuthenticator. The cert is only *requested* when ClientCertificateMode is set on the HTTPS
// endpoint (Helm sets AllowCertificate when viewApi is enabled), so admission-webhook calls — which
// present no client cert — are unaffected.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ConfigureHttpsDefaults(https =>
        https.ClientCertificateValidation = (_, _, _) => true));

// Register the home-grown operator runtime with auto-discovery (controllers/finalizers/validators).
// Leader election (Lease-based) makes scaling the operator beyond 1 replica safe:
// only the leader reconciles, preventing concurrent destructive DDL (TOCTOU on AG membership).
builder.Services.AddOperator(options =>
{
    options.LeaderElection = true;
    options.Assemblies = [typeof(Program).Assembly];
});

// SQL Services
builder.Services.Configure<SqlConnectionOptions>(builder.Configuration.GetSection("SqlConnection"));
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISqlServerManager, SqlServerManager>();
builder.Services.AddSingleton<ISqlUserManager, SqlUserManager>();
builder.Services.AddSingleton<ISqlAgentManager, SqlAgentManager>();
builder.Services.AddSingleton<ISqlTdeManager, SqlTdeManager>();
builder.Services.AddSingleton<ISqlAgManager, SqlAgManager>();
builder.Services.AddSingleton<ISqlStoredProcedureManager, SqlStoredProcedureManager>();
builder.Services.AddSingleton<IDatabaseMailManager, DatabaseMailManager>();
builder.Services.AddSingleton<IServiceMasterKeyManager, ServiceMasterKeyManager>();

// Target resolution
builder.Services.AddSingleton<ITargetResolver, TargetResolver>();

// Kubernetes builders
builder.Services.AddSingleton<IAntiAffinityBuilder, AntiAffinityBuilder>();
builder.Services.AddSingleton<IStatefulSetBuilder, StatefulSetBuilder>();
builder.Services.AddSingleton<IServiceBuilder, ServiceBuilder>();
builder.Services.AddSingleton<ICiliumNetworkPolicyBuilder, CiliumNetworkPolicyBuilder>();
builder.Services.AddSingleton<IMssqlConfBuilder, MssqlConfBuilder>();
builder.Services.AddSingleton<IMachineKeyManager, MachineKeyManager>();
builder.Services.AddSingleton<IAgPodManager, AgPodManager>();

// Filters & Health
builder.Services.AddSingleton<IDatabaseFilter, DatabaseFilter>();
builder.Services.AddSingleton<ISqlHealthChecker, SqlHealthChecker>();
builder.Services.AddSingleton<IAgHealthChecker, AgHealthChecker>();
builder.Services.AddSingleton<IAgFailoverOrchestrator, AgFailoverOrchestrator>();
builder.Services.AddSingleton<IAgRollingUpdateOrchestrator, AgRollingUpdateOrchestrator>();
builder.Services.AddSingleton(TimeProvider.System);

// Shared HTTP resilience: per-attempt timeout, exponential retry with jitter on
// transient failures only (5xx/408/network — 4xx is never retried), bounded total time.
void ConfigureResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions options)
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
    options.Retry.MaxRetryAttempts = 3;
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
}

// GitHub
builder.Services.AddHttpClient<IGitHubContentFetcher, GitHubContentFetcher>()
    .AddStandardResilienceHandler(ConfigureResilience);

// S3 & Backup
builder.Services.AddHttpClient<IS3Client, S3Client>()
    .AddStandardResilienceHandler(ConfigureResilience);
builder.Services.AddSingleton<IBackupEncryptor, BackupEncryptor>();

// Events & Metrics
builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
builder.Services.AddSingleton<OperatorMetrics>();
builder.Services.AddSingleton<MsSqlOperator.Operator.Abstractions.IReconcileMetrics>(sp => sp.GetRequiredService<OperatorMetrics>());

// Aggregated read-only view API (view.mssql-operator.evariops.com) — live per-item detail from SQL, never etcd
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.FrontProxyAuthenticator>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.AggregatorAuthorizer>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.IViewResourceProvider, MsSqlOperator.Operator.ViewApi.TdeDatabaseStatesProvider>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.IViewResourceProvider, MsSqlOperator.Operator.ViewApi.StoredProcedureDeploymentsProvider>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.IViewResourceProvider, MsSqlOperator.Operator.ViewApi.SqlUserTargetsProvider>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.IViewResourceProvider, MsSqlOperator.Operator.ViewApi.SqlAgentJobRunsProvider>();
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.IViewResourceProvider, MsSqlOperator.Operator.ViewApi.AgReplicaStatesProvider>();

// Failover Breakout Controller
builder.Services.Configure<BreakoutOptions>(builder.Configuration.GetSection(BreakoutOptions.SectionName));
// Prefer the operator's own namespace (downward API: POD_NAMESPACE) for the blacklist ConfigMap so
// state isn't fragmented into 'default' for custom installs.
builder.Services.PostConfigure<BreakoutOptions>(o =>
{
    var podNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE");
    if (!string.IsNullOrWhiteSpace(podNamespace))
        o.BlacklistConfigMapNamespace = podNamespace;
});
var breakoutOptions = builder.Configuration.GetSection(BreakoutOptions.SectionName).Get<BreakoutOptions>();
if (breakoutOptions?.Enabled == true)
{
    builder.Services.AddSingleton<BreakoutMetrics>();
    builder.Services.AddSingleton<IBlacklistManager, BlacklistManager>();

    if (string.Equals(breakoutOptions.FencingDriver, "none", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddSingleton<IFencingDriver, NoneFencingDriver>();
    else
        builder.Services.AddSingleton<IFencingDriver, VolumeAttachmentFencingDriver>();

    builder.Services.AddHostedService<FailoverBreakoutService>();
}

// OpenTelemetry Prometheus metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(OperatorMetrics.MeterName)
            .AddPrometheusExporter();
        if (breakoutOptions?.Enabled == true)
            metrics.AddMeter(BreakoutMetrics.MeterName);
    });

// Readiness reflects real API-server connectivity; liveness stays a plain process probe.
builder.Services.AddHealthChecks()
    .AddCheck<MsSqlOperator.Services.Health.ApiServerHealthCheck>("kubernetes-api", tags: ["ready"]);

var app = builder.Build();

app.UseRouting();
app.MapValidationWebhooks();
app.MapViewApi();
// Liveness: process is up (no API dependency, so a transient API blip won't trigger a restart).
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
// Readiness: only ready when the API server is reachable.
app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapPrometheusScrapingEndpoint();

await app.RunAsync();
return 0;
