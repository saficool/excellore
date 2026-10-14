using Excellore.Auth;
using Excellore.Authz;
using Excellore.Platform;
using Excellore.Sample.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var localDevelopment = builder.Environment.IsDevelopment() &&
    string.Equals(configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true", StringComparison.OrdinalIgnoreCase);

builder.Services.AddExcelloreAuthentication(configuration, "api-__APP_SLUG__", !localDevelopment);
builder.Services.AddExcelloreAuthz();
builder.Services.AddSingleton<IPrivilegeClassifier, SamplePrivilegeClassifier>();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    configuration["Valkey:ConnectionString"] ?? throw new InvalidOperationException("Valkey configuration is required.")));
builder.Services.AddValkeyAuthzResolver(configuration, "__APP_SLUG__", localDevelopment);
builder.Services.AddRabbitMqAuthzChangedConsumer("__APP_SLUG__", new Uri(
    configuration["Events:BrokerUri"] ?? throw new InvalidOperationException("Event broker configuration is required.")));
builder.Services.AddDbContext<SampleDbContext>(options => options.UseNpgsql(
    configuration.GetConnectionString("App") ?? throw new InvalidOperationException("App database configuration is required.")));
builder.Services.AddScoped<TenantUnitOfWork<SampleDbContext>>();
builder.Services.AddExcelloreHealthChecks().AddCheck<SampleDatabaseHealthCheck>("database");
builder.Services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource(ExcelloreTelemetry.ActivitySourceName);
    if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) tracing.AddOtlpExporter();
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapExcelloreHealthChecks();

var tenant = app.MapGroup("/v1/tenants/{tenantId:guid}").AddTenantGate();
tenant.MapGet("/items/{itemId:guid}", async (Guid itemId, HttpContext http, SampleDbContext db,
    TenantUnitOfWork<SampleDbContext> unitOfWork, CancellationToken cancellationToken) =>
{
    var item = await unitOfWork.RunAsync(() => db.Items.AsNoTracking()
        .Where(row => row.Id == itemId)
        .Select(row => new { row.Id, row.Code })
        .SingleOrDefaultAsync(cancellationToken), cancellationToken);
    return item is null ? ExcelloreProblems.NotFound(http) : Results.Ok(item);
}).RequirePermission("__APP_SLUG__.item.read");

app.AssertTenantRoutesProtected();
app.Run();

public partial class Program { }
