using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Excellore.Platform;

/// <summary>RabbitMQ consumer transport with manual acknowledgements and a dead-letter queue.</summary>
public sealed class RabbitMqEventBus : IEventBus
{
    private const string EventExchange = "excellore.events";
    private const string DeadExchange = "excellore.dead";
    private const string TraceParentHeader = "traceparent";
    private const int MaxBodyBytes = 256 * 1024;
    private readonly ConnectionFactory _factory;

    public RabbitMqEventBus(Uri brokerUri)
    {
        if (brokerUri is null || !brokerUri.IsAbsoluteUri ||
            brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException("An absolute AMQP broker URI is required.", nameof(brokerUri));
        }

        _factory = new ConnectionFactory
        {
            Uri = brokerUri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };
    }

    public async Task<IEventPublisher> CreatePublisherAsync(CancellationToken cancellationToken = default)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        try
        {
            connection = await _factory.CreateConnectionAsync(cancellationToken);
            channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true), cancellationToken);
            await channel.ExchangeDeclareAsync(EventExchange, ExchangeType.Topic,
                durable: true, autoDelete: false, cancellationToken: cancellationToken);
            return new Publisher(connection, channel);
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string serviceName,
        IReadOnlyDictionary<string, PlatformEventHandler> handlers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName) ||
            serviceName.Any(character => !char.IsAsciiLetterLower(character) &&
                                         !char.IsAsciiDigit(character) && character != '-'))
        {
            throw new ArgumentException("A lowercase service name is required.", nameof(serviceName));
        }
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Count == 0 || handlers.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Value is null ||
                item.Key.Any(character => !char.IsAsciiLetterLower(character) &&
                                          !char.IsAsciiDigit(character) &&
                                          character is not ('.' or '-'))))
        {
            throw new ArgumentException("At least one event handler is required.", nameof(handlers));
        }

        IConnection? connection = null;
        IChannel? channel = null;
        var lifetime = new CancellationTokenSource();
        try
        {
            connection = await _factory.CreateConnectionAsync(cancellationToken);
            channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await channel.ExchangeDeclareAsync(EventExchange, ExchangeType.Topic,
                durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct,
                durable: true, autoDelete: false, cancellationToken: cancellationToken);

            var queue = $"excellore.{serviceName}.events";
            var deadQueue = $"{queue}.dead";
            var queueArguments = new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = DeadExchange,
                ["x-dead-letter-routing-key"] = serviceName,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish"
            };
            await channel.QueueDeclareAsync(deadQueue, durable: true, exclusive: false,
                autoDelete: false, arguments: new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum"
                }, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(deadQueue, DeadExchange, serviceName,
                cancellationToken: cancellationToken);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false,
                autoDelete: false, arguments: queueArguments, cancellationToken: cancellationToken);
            foreach (var eventType in handlers.Keys)
            {
                await channel.QueueBindAsync(queue, EventExchange, eventType,
                    cancellationToken: cancellationToken);
            }

            await channel.BasicQosAsync(0, 1, false, cancellationToken);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                if (delivery.Body.Length > MaxBodyBytes ||
                    !handlers.TryGetValue(delivery.RoutingKey, out var handler))
                {
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                    return;
                }

                // Delivery bodies are valid only during this callback; copy before awaiting the handler.
                var body = delivery.Body.ToArray();
                using var activity = ExcelloreTelemetry.Source.StartActivity(
                    "messaging.process", ActivityKind.Consumer, ReadParentContext(delivery.BasicProperties));
                activity?.SetTag("messaging.system", "rabbitmq");
                activity?.SetTag("messaging.destination.name", EventExchange);
                var delays = new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1) };
                var handled = false;
                for (var attempt = 0; attempt <= delays.Length; attempt++)
                {
                    if (lifetime.IsCancellationRequested)
                    {
                        return; // The unacknowledged delivery returns to the queue on shutdown.
                    }
                    try
                    {
                        await handler(body, lifetime.Token);
                        handled = true;
                        break;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception) when (attempt < delays.Length)
                    {
                        try
                        {
                            await Task.Delay(delays[attempt], lifetime.Token);
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }

                if (handled)
                {
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                }
            };
            var tag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer,
                cancellationToken: cancellationToken);
            return new Subscription(connection, channel, tag, lifetime);
        }
        catch
        {
            lifetime.Dispose();
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
            throw;
        }
    }

    private static ActivityContext ReadParentContext(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null)
        {
            return default;
        }

        foreach (var header in properties.Headers)
        {
            if (!string.Equals(header.Key, TraceParentHeader, StringComparison.OrdinalIgnoreCase) ||
                header.Value is not byte[] bytes || bytes.Length is < 55 or > 128)
            {
                continue;
            }

            if (ActivityContext.TryParse(Encoding.ASCII.GetString(bytes), null, out var context))
            {
                return context;
            }
        }

        return default;
    }

    private sealed class Subscription(IConnection connection, IChannel channel, string consumerTag,
        CancellationTokenSource lifetime) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            try
            {
                if (channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag);
                }
            }
            finally
            {
                await channel.DisposeAsync();
                await connection.DisposeAsync();
                lifetime.Dispose();
            }
        }
    }

    private sealed class Publisher(IConnection connection, IChannel channel) : IEventPublisher
    {
        private readonly SemaphoreSlim _publishGate = new(1, 1);

        public async Task PublishAsync(Guid eventId, string eventType, ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
        {
            if (eventId == Guid.Empty)
            {
                throw new ArgumentException("A valid event ID is required.", nameof(eventId));
            }
            if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 128 ||
                eventType.Any(character => !char.IsAsciiLetterLower(character) &&
                                           !char.IsAsciiDigit(character) &&
                                           character is not ('.' or '-')))
            {
                throw new ArgumentException("A valid event type is required.", nameof(eventType));
            }
            if (body.IsEmpty || body.Length > MaxBodyBytes)
            {
                throw new ArgumentException("The event body size is invalid.", nameof(body));
            }

            using var activity = ExcelloreTelemetry.Source.StartActivity(
                "messaging.publish", ActivityKind.Producer);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", EventExchange);
            await _publishGate.WaitAsync(cancellationToken);
            try
            {
                var traceActivity = activity ?? Activity.Current;
                var traceParent = traceActivity?.IdFormat == ActivityIdFormat.W3C
                    ? traceActivity.Id
                    : null;
                await channel.BasicPublishAsync(EventExchange, eventType, mandatory: true,
                    new BasicProperties
                    {
                        Persistent = true,
                        ContentType = "application/cloudevents+json",
                        MessageId = eventId.ToString("D"),
                        Type = eventType,
                        Headers = traceParent is null ? null : new Dictionary<string, object?>
                        {
                            [TraceParentHeader] = Encoding.ASCII.GetBytes(traceParent)
                        }
                    }, body, cancellationToken);
            }
            catch (Exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }
            finally
            {
                _publishGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _publishGate.WaitAsync();
            try
            {
                await channel.DisposeAsync();
                await connection.DisposeAsync();
            }
            finally
            {
                _publishGate.Release();
                _publishGate.Dispose();
            }
        }
    }
}
