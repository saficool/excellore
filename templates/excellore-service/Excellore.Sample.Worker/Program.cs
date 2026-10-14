using Excellore.Platform;
using Excellore.Sample.Infrastructure;
using Excellore.Sample.Worker;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddExcelloreWorkerTenantScope();
builder.Services.AddDbContext<SampleDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("App") ??
    throw new InvalidOperationException("App database configuration is required.")));
builder.Services.AddSingleton<PostgresEventInbox>();
builder.Services.AddSingleton<PostgresEventOutbox>();
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
builder.Services.AddSingleton<IEventBus>(_ => new RabbitMqEventBus(new Uri(
    builder.Configuration["Events:BrokerUri"] ??
    throw new InvalidOperationException("Event broker configuration is required."))));
builder.Services.AddHostedService<Worker>();
builder.Services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource(ExcelloreTelemetry.ActivitySourceName);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) tracing.AddOtlpExporter();
});
await builder.Build().RunAsync();
