using Excellore.Platform;
using RabbitMQ.Client;
using System.Text.Json;
using Testcontainers.RabbitMq;

namespace Excellore.Platform.Tests;

public sealed class RabbitMqEventBusTests
{
    private const string RabbitRepository = "rabbitmq";
    private const string ImageRelease = "4.3.5-management";
    private const string EventType = "access.authz.changed.v1";

    [Fact]
    public async Task Publisher_confirms_persistent_routed_events_and_rejects_unroutable_events()
    {
        await using var container = new RabbitMqBuilder($"{RabbitRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        var brokerUri = new Uri(container.GetConnectionString());
        var bus = new RabbitMqEventBus(brokerUri);
        await using var publisher = await bus.CreatePublisherAsync();
        await using var connection = await new ConnectionFactory { Uri = brokerUri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        const string queue = "excellore.publisher-test.events";
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
        await channel.QueueBindAsync(queue, "excellore.events", EventType);

        var eventId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = eventId,
            type = EventType,
            tenantid = Guid.NewGuid()
        });
        await publisher.PublishAsync(eventId, EventType, body);
        var delivered = await channel.BasicGetAsync(queue, autoAck: true);
        Assert.NotNull(delivered);
        Assert.Equal(body, delivered.Body.ToArray());
        Assert.Equal(eventId.ToString("D"), delivered.BasicProperties.MessageId);
        Assert.Equal(EventType, delivered.BasicProperties.Type);
        Assert.Equal("application/cloudevents+json", delivered.BasicProperties.ContentType);
        Assert.True(delivered.BasicProperties.Persistent);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(
            Guid.NewGuid(), "profile.updated.v1", body));
        Assert.Null(await channel.BasicGetAsync(queue, autoAck: true));
    }

    [Fact]
    public async Task Successful_handler_is_acknowledged_and_failed_handler_goes_to_dead_letter_queue()
    {
        await using var container = new RabbitMqBuilder($"{RabbitRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        var brokerUri = new Uri(container.GetConnectionString());
        var bus = new RabbitMqEventBus(brokerUri);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await bus.SubscribeAsync("profile",
                         new Dictionary<string, PlatformEventHandler>
                         {
                             [EventType] = (body, _) =>
                             {
                                 Assert.Equal("ok"u8.ToArray(), body.ToArray());
                                 received.TrySetResult();
                                 return Task.CompletedTask;
                             }
                         }))
        {
            await PublishAsync(brokerUri, "ok"u8.ToArray());
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using var connection = await new ConnectionFactory { Uri = brokerUri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.Null(await channel.BasicGetAsync("excellore.profile.events", autoAck: false));

        var attempts = 0;
        await using (await bus.SubscribeAsync("access",
                         new Dictionary<string, PlatformEventHandler>
                         {
                             [EventType] = (_, _) =>
                             {
                                 Interlocked.Increment(ref attempts);
                                 throw new InvalidOperationException("Synthetic failure.");
                             }
                         }))
        {
            await PublishAsync(brokerUri, "fail"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            BasicGetResult? dead;
            do
            {
                dead = await channel.BasicGetAsync("excellore.access.events.dead", autoAck: true,
                    cancellationToken: timeout.Token);
                if (dead is null) await Task.Delay(50, timeout.Token);
            } while (dead is null);
            Assert.Equal("fail"u8.ToArray(), dead.Body.ToArray());
            Assert.Equal(3, Volatile.Read(ref attempts));
            Assert.Null(await channel.BasicGetAsync("excellore.access.events", autoAck: false));
        }
    }

    private static async Task PublishAsync(Uri brokerUri, byte[] body)
    {
        await using var connection = await new ConnectionFactory { Uri = brokerUri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync("excellore.events", EventType, mandatory: false,
            new BasicProperties { Persistent = true }, body);
    }
}
