using KubeOps.Operator;
using MsSqlOperator.Services.Backup;
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
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISqlServerManager, SqlServerManager>();
builder.Services.AddSingleton<ISqlUserManager, SqlUserManager>();
builder.Services.AddSingleton<ISqlAgentManager, SqlAgentManager>();
builder.Services.AddSingleton<ISqlTdeManager, SqlTdeManager>();
builder.Services.AddSingleton<ISqlAgManager, SqlAgManager>();
builder.Services.AddSingleton<ISqlStoredProcedureManager, SqlStoredProcedureManager>();
builder.Services.AddSingleton<IDatabaseMailManager, DatabaseMailManager>();

// Target resolution
builder.Services.AddSingleton<ITargetResolver, TargetResolver>();

// Kubernetes builders
builder.Services.AddSingleton<IAntiAffinityBuilder, AntiAffinityBuilder>();
builder.Services.AddSingleton<IStatefulSetBuilder, StatefulSetBuilder>();
builder.Services.AddSingleton<IServiceBuilder, ServiceBuilder>();
builder.Services.AddSingleton<ICiliumNetworkPolicyBuilder, CiliumNetworkPolicyBuilder>();
builder.Services.AddSingleton<IMssqlConfBuilder, MssqlConfBuilder>();
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

// OpenTelemetry Prometheus metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(OperatorMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseRouting();
app.MapControllers();
app.MapPrometheusScrapingEndpoint();

await app.RunAsync();
