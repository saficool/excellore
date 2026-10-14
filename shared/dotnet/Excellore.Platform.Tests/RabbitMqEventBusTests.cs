using Excellore.Platform;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Excellore.Platform.Tests;

public sealed class RabbitMqEventBusTests
{
    private const string RabbitRepository = "rabbitmq";
    private const string ImageRelease = "4.3.5-management";
    private const string EventType = "access.authz.changed.v1";

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
