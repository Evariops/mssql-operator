using KubeOps.Operator;
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

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Register KubeOps operator with auto-discovery
builder.Services
    .AddKubernetesOperator()
    .RegisterComponents();

// ASP.NET MVC for webhook endpoints
builder.Services.AddControllers();

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

// GitHub
builder.Services.AddHttpClient<IGitHubContentFetcher, GitHubContentFetcher>();

// S3 & Backup
builder.Services.AddHttpClient<IS3Client, S3Client>();
builder.Services.AddSingleton<IBackupEncryptor, BackupEncryptor>();

// Events & Metrics
builder.Services.AddSingleton<IEventPublisher, EventPublisher>();
builder.Services.AddSingleton<OperatorMetrics>();

// Failover Breakout Controller
builder.Services.Configure<BreakoutOptions>(builder.Configuration.GetSection(BreakoutOptions.SectionName));
var breakoutOptions = builder.Configuration.GetSection(BreakoutOptions.SectionName).Get<BreakoutOptions>();
if (breakoutOptions?.Enabled == true)
{
    builder.Services.AddSingleton<BreakoutMetrics>();
    builder.Services.AddSingleton<IBlacklistManager, BlacklistManager>();
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

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

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");
app.MapPrometheusScrapingEndpoint();

await app.RunAsync();
