using Excellore.Profile.Worker;
using Excellore.Platform;
using Excellore.Authz;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("ProfilePublisher")
    ?? throw new InvalidOperationException("ConnectionStrings:ProfilePublisher is required.");
var brokerAddress = builder.Configuration["Messaging:RabbitMqUri"];
if (!Uri.TryCreate(brokerAddress, UriKind.Absolute, out var brokerUri))
    throw new InvalidOperationException("Messaging:RabbitMqUri is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IEventBus>(new RabbitMqEventBus(brokerUri));
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
var workerConnectionString = builder.Configuration.GetConnectionString("ProfileWorker")
    ?? throw new InvalidOperationException("ConnectionStrings:ProfileWorker is required.");
builder.Services.AddSingleton(new ProfileWorkerDatabase(NpgsqlDataSource.Create(workerConnectionString)));
builder.Services.AddSingleton<PostgresEventInbox>();
builder.Services.AddSingleton<ProfileMembershipProjector>();
builder.Services.AddSingleton<ProfileTenantErasureProjector>();
builder.Services.AddKeycloakDirectory(builder.Configuration, "worker-profile");
builder.Services.AddSingleton<ProfileIdentityMirrorProjector>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ProfileMembershipSubscriber>();

var host = builder.Build();
host.Run();
