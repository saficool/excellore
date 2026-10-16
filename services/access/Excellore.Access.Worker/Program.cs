using Excellore.Access.Worker;
using Excellore.Access.Infrastructure;
using Excellore.Platform;
using Excellore.Authz;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AccessPublisher")
    ?? throw new InvalidOperationException("ConnectionStrings:AccessPublisher is required.");
var brokerAddress = builder.Configuration["Messaging:RabbitMqUri"];
if (!Uri.TryCreate(brokerAddress, UriKind.Absolute, out var brokerUri))
    throw new InvalidOperationException("Messaging:RabbitMqUri is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IEventBus>(new RabbitMqEventBus(brokerUri));
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
builder.Services.AddSingleton<PostgresEventInbox>();
builder.Services.AddSingleton<ProfileErasureConfirmationProjector>();
builder.Services.AddHostedService<AccessLifecycleSubscriber>();
builder.Services.AddHostedService<Worker>();
var workerConnectionString = builder.Configuration.GetConnectionString("AccessWorker")
    ?? throw new InvalidOperationException("ConnectionStrings:AccessWorker is required.");
builder.Services.AddSingleton(new AccessWorkerDatabase(NpgsqlDataSource.Create(workerConnectionString)));
builder.Services.AddExcelloreWorkerTenantScope();
builder.Services.AddScoped(provider => new AccessExpiryService(
    provider.GetRequiredService<AccessWorkerDatabase>().Source,
    provider.GetRequiredService<ITenantIdAccessor>()));
builder.Services.AddSingleton<IAuditExportObjectStore, S3AuditExportObjectStore>();
builder.Services.AddScoped(provider => new AuditExportBuilder(
    provider.GetRequiredService<AccessWorkerDatabase>().Source,
    provider.GetRequiredService<ITenantIdAccessor>(),
    provider.GetRequiredService<IAuditExportObjectStore>()));
builder.Services.AddScoped<AccessTenantMaintenanceJob>();
builder.Services.AddScoped(provider => new TenantDeletionDispatchService(
    provider.GetRequiredService<AccessWorkerDatabase>().Source,
    provider.GetRequiredService<ITenantIdAccessor>()));
builder.Services.AddScoped(provider => new TenantErasureFinalizer(
    provider.GetRequiredService<AccessWorkerDatabase>().Source,
    provider.GetRequiredService<ITenantIdAccessor>(),
    provider.GetRequiredService<IAuditExportObjectStore>()));
builder.Services.AddScoped(provider => new RoleAssignmentBatchProcessor(
    provider.GetRequiredService<AccessWorkerDatabase>().Source,
    provider.GetRequiredService<ITenantIdAccessor>()));
builder.Services.AddHostedService<AccessExpiryScheduler>();
builder.Services.AddKeycloakDirectory(builder.Configuration, "worker-access");
builder.Services.AddHostedService<AccessIdentityDriftWorker>();

var host = builder.Build();
host.Run();
