using MsSqlOperator.Operator.Client;
using MsSqlOperator.Operator.Codegen;
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

// Code generation mode (replaces `kubeops generate`): `dotnet run -- generate <output-dir>`.
if (args.Length > 0 && args[0] == "generate")
    return await CodegenRunner.RunAsync(args);

// Refreshes the snapshot of the tags MCR publishes: `dotnet run -- refresh-images [output-file]`.
// Never called at runtime — admission must not depend on the registry being reachable.
if (args.Length > 0 && args[0] == "refresh-images")
    return await ImageCatalogRefresher.RunAsync(args);

var builder = WebApplication.CreateBuilder(args);

// Built-in console logging. Levels are driven by the `Logging` configuration section
// (e.g. the env var Logging__LogLevel__Default the Helm chart sets), so verbosity changes without
// a rebuild. `Logging:Format=json` (Helm: logFormat) switches to structured JSON lines for
// log pipelines; both formats carry the per-reconcile scope (kind, key, reconcileId) opened by the
// runtime, so every line of one pass can be correlated.
builder.Logging.ClearProviders();
// The kubelet probes /healthz and /readyz every few seconds on each pod: at Information, ASP.NET
// Core logged four lines per probe and buried the operator's own logs. Default it to Warning, as
// the lowest-precedence source so Logging__LogLevel__Microsoft.AspNetCore still overrides it.
builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning" },
});
if (string.Equals(builder.Configuration["Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
    });
}
else
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.IncludeScopes = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}

// The aggregated view API authenticates the kube-aggregator by its front-proxy client certificate,
// which is issued by the cluster's requestheader CA — NOT by anything in the operator's trust store.
// Kestrel's default client-certificate validation would therefore reject it at the TLS layer (the
// aggregator sees "unexpected EOF" → FailedDiscoveryCheck). We accept any client cert at the TLS layer
// and defer the real trust decision (chain to requestheader-client-ca + allowed-names) to
// FrontProxyAuthenticator. The cert is only *requested* when ClientCertificateMode is set on the HTTPS
// endpoint (Helm sets AllowCertificate when viewApi is enabled), so admission-webhook calls — which
// present no client cert — are unaffected.
//
// With Webhook:CertificateDirectory (Helm sets it), the serving certificate comes from the
// mounted Secret and is reloaded when the Secret is renewed — no restart, no window in which the API
// server rejects the certificate still served.
var certificateDirectory = builder.Configuration["Webhook:CertificateDirectory"];
var servingCertificate = string.IsNullOrEmpty(certificateDirectory)
    ? null
    : new ServingCertificate(certificateDirectory, TimeProvider.System);
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateValidation = (_, _, _) => true;
        if (servingCertificate is not null)
            https.ServerCertificateSelector = (_, _) => servingCertificate.Current;
    }));

// Operator:Role (see OperatorOptions.Role) — the breakout controller can run in a dedicated
// Deployment, with its own ServiceAccount, instead of inside the process that serves the webhook.
var role = builder.Configuration["Operator:Role"] ?? "All";
var runsControllers = !role.Equals("Breakout", StringComparison.OrdinalIgnoreCase);
var runsBreakout = !role.Equals("Operator", StringComparison.OrdinalIgnoreCase);

// Register the home-grown operator runtime with auto-discovery (controllers/finalizers/validators).
// Leader election (Lease-based) makes scaling the operator beyond 1 replica safe:
// only the leader reconciles, preventing concurrent destructive DDL (TOCTOU on AG membership).
if (runsControllers)
{
    builder.Services.AddOperator(options =>
    {
        options.LeaderElection = true;
        options.Assemblies = [typeof(Program).Assembly];

        // The worker count (and the per-pass timeout) must be tunable at deploy time — the
        // RTO of a failover degrades with fleet size, and 2 hard-coded workers per kind were the ceiling.
        // Configure via the Operator section (Helm: env Operator__WorkerCount).
        builder.Configuration.GetSection("Operator").Bind(options);
    });
}
else
{
    builder.Services.AddKubernetesClient();
}

// SQL Services
builder.Services.Configure<SqlConnectionOptions>(builder.Configuration.GetSection("SqlConnection"));
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISqlServerManager, SqlServerManager>();
builder.Services.AddSingleton<ISqlUserManager, SqlUserManager>();
builder.Services.AddSingleton<ISqlAgentManager, SqlAgentManager>();
builder.Services.AddSingleton<ISqlTdeManager, SqlTdeManager>();
builder.Services.AddSingleton<ISqlAgManager, SqlAgManager>();
builder.Services.AddSingleton<ISqlStoredProcedureManager, SqlStoredProcedureManager>();
builder.Services.AddSingleton<IProcDeployerLoginManager, ProcDeployerLoginManager>();
builder.Services.AddSingleton<IDatabaseMailManager, DatabaseMailManager>();
builder.Services.AddSingleton<IServiceMasterKeyManager, ServiceMasterKeyManager>();
builder.Services.AddSingleton<IFullTextManager, FullTextManager>();

// Target resolution
builder.Services.AddSingleton<ITargetResolver, TargetResolver>();

// Kubernetes builders
builder.Services.AddSingleton<IAntiAffinityBuilder, AntiAffinityBuilder>();
builder.Services.AddSingleton<ITopologySpreadBuilder, TopologySpreadBuilder>();
builder.Services.AddSingleton<IStatefulSetBuilder, StatefulSetBuilder>();
builder.Services.AddSingleton<IServiceBuilder, ServiceBuilder>();
builder.Services.AddSingleton<ICiliumNetworkPolicyBuilder, CiliumNetworkPolicyBuilder>();
builder.Services.AddSingleton<IMssqlConfBuilder, MssqlConfBuilder>();
builder.Services.AddSingleton<IMachineKeyManager, MachineKeyManager>();
builder.Services.AddSingleton<IAgPodManager, AgPodManager>();
builder.Services.AddSingleton<ISaPasswordConverger, SaPasswordConverger>();
builder.Services.AddSingleton<IRolloutBudget, RolloutBudget>();

// Filters & Health
builder.Services.AddSingleton<IDatabaseFilter, DatabaseFilter>();
builder.Services.AddSingleton<IAgHealthChecker, AgHealthChecker>();
builder.Services.AddSingleton<IAgFailoverOrchestrator, AgFailoverOrchestrator>();
builder.Services.AddSingleton<IAgRollingUpdateOrchestrator, AgRollingUpdateOrchestrator>();
builder.Services.AddSingleton(TimeProvider.System);

// Fencing: the failover orchestrator must be able to PROVE that an unreachable old
// primary cannot write before promoting anyone, so the fencing driver is wired independently of the
// optional breakout controller. fencingDriver=none keeps the dry-run driver: nothing is ever
// detached, so fencing is never confirmed and a failover whose old primary is unreachable is
// refused rather than performed blind.
var fencingDriverName = builder.Configuration.GetSection(BreakoutOptions.SectionName).Get<BreakoutOptions>()?.FencingDriver;
if (string.Equals(fencingDriverName, "none", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IFencingDriver, NoneFencingDriver>();
else
    builder.Services.AddSingleton<IFencingDriver, VolumeAttachmentFencingDriver>();
builder.Services.AddSingleton<IReplicaFencer, ReplicaFencer>();

// Shared HTTP resilience: per-attempt timeout, exponential retry with jitter on
// transient failures only (5xx/408/network — 4xx is never retried), bounded total time.
void ConfigureResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions options)
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
    options.Retry.MaxRetryAttempts = 3;
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
}

// GitHub
builder.Services.AddSingleton<GitHubContentCache>();
builder.Services.AddHttpClient<IGitHubContentFetcher, GitHubContentFetcher>()
    .AddStandardResilienceHandler(ConfigureResilience);

// S3 & Backup
builder.Services.AddHttpClient<IS3Client, S3Client>()
    // The endpoint comes from the CR — no loopback, link-local or metadata-service target.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectCallback = S3EndpointGuard.ConnectAsync })
    .AddStandardResilienceHandler(ConfigureResilience);
builder.Services.AddSingleton<IBackupEncryptor, BackupEncryptor>();

// Events & Metrics
builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
builder.Services.AddSingleton<OperatorMetrics>();
builder.Services.AddSingleton<MsSqlOperator.Operator.Abstractions.IReconcileMetrics>(sp => sp.GetRequiredService<OperatorMetrics>());

// Aggregated read-only view API (view.mssql-operator.evariops.com) — live per-item detail from SQL, never etcd
builder.Services.AddSingleton<MsSqlOperator.Operator.ViewApi.ViewQueryGate>();
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
var breakoutHere = runsBreakout && breakoutOptions?.Enabled == true;
if (breakoutHere)
{
    builder.Services.AddSingleton<BreakoutMetrics>();
    builder.Services.AddSingleton<IBlacklistManager, BlacklistManager>();
    builder.Services.AddHostedService<FailoverBreakoutService>();
}

// OpenTelemetry Prometheus metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(OperatorMetrics.MeterName)
            .AddPrometheusExporter();
        if (breakoutHere)
            metrics.AddMeter(BreakoutMetrics.MeterName);
    });

// Readiness reflects real API-server connectivity; liveness stays a plain process probe.
var health = builder.Services.AddHealthChecks()
    .AddCheck<MsSqlOperator.Services.Health.ApiServerHealthCheck>("kubernetes-api", tags: ["ready"]);
// A zombie owned-resource watch takes the replica out of the webhook/view-API endpoints.
if (runsControllers)
    health.AddCheck<MsSqlOperator.Services.Health.OwnedWatchHealthCheck>("owned-resource-watch", tags: ["ready"]);

var app = builder.Build();

app.UseRouting();

// Admission and view-API endpoints exist ONLY on the TLS listener. Without this
// guard they also answered in clear text on the :8080 metrics port — the trust model
// (front-proxy client certs, webhook TLS) only holds on HTTPS.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (!ctx.Request.IsHttps
        && (path.StartsWithSegments("/validate") || path.StartsWithSegments("/apis") || path.StartsWithSegments("/openapi")))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    await next();
});

if (runsControllers)
{
    app.MapValidationWebhooks();
    app.MapViewApi();
}
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
