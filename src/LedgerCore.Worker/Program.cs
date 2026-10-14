using LedgerCore.Application;
using LedgerCore.Application.Common;
using LedgerCore.Infrastructure;
using LedgerCore.Infrastructure.Outbox;
using LedgerCore.Worker;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

builder.Services.AddLedgerCoreApplication();
builder.Services.AddLedgerCoreInfrastructure();
builder.Services.AddOptions<WorkerOptions>().BindConfiguration(WorkerOptions.SectionName);
builder.Services.AddOptions<WorkerFailureInjectionOptions>().BindConfiguration(WorkerFailureInjectionOptions.SectionName);

// Optional crash injection: wraps the real sink. Validated below to be Development/Testing only.
builder.Services.AddSingleton<DatabaseNotificationSink>();
builder.Services.AddSingleton<INotificationSink>(provider =>
{
    var inner = provider.GetRequiredService<DatabaseNotificationSink>();
    var killAfter = provider.GetRequiredService<IOptions<WorkerFailureInjectionOptions>>().Value.KillAfterDeliveries;
    return killAfter > 0 ? new CrashAfterDeliveriesSink(inner, killAfter) : inner;
});

builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<IdempotencyCleanupWorker>();
builder.Services.AddHostedService<ReconciliationWorker>();

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("ledgercore-worker"))
    .WithTracing(tracing => tracing.AddSource(LedgerTelemetry.SourceName).AddNpgsql())
    .WithMetrics(metrics => metrics.AddMeter(LedgerTelemetry.SourceName).AddRuntimeInstrumentation().AddMeter("Npgsql"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

var host = builder.Build();

var environment = host.Services.GetRequiredService<IHostEnvironment>();
var developmentLike = environment.IsDevelopment() || environment.IsEnvironment("Testing");
if (!developmentLike && host.Services.GetRequiredService<IOptions<WorkerFailureInjectionOptions>>().Value.KillAfterDeliveries > 0)
{
    throw new InvalidOperationException("WorkerFailureInjection is restricted to Development and Testing.");
}

// Fail fast on a missing connection string instead of failing inside the first poll.
_ = host.Services.GetRequiredService<NpgsqlDataSource>();

await host.RunAsync();
