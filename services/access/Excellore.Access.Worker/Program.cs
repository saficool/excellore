using Excellore.Access.Worker;
using Excellore.Platform;
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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
