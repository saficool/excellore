using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    public async Task Publisher_confirms_persistent_routed_events_and_rejects_unroutable_events()
    {
        using var listener = TraceListener();
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
        using var parent = new Activity("api-request").Start();
        parent.AddBaggage("private-value", "must-not-propagate");
        await publisher.PublishAsync(eventId, EventType, body);
        var delivered = await channel.BasicGetAsync(queue, autoAck: true);
        Assert.NotNull(delivered);
        Assert.Equal(body, delivered.Body.ToArray());
        Assert.Equal(eventId.ToString("D"), delivered.BasicProperties.MessageId);
        Assert.Equal(EventType, delivered.BasicProperties.Type);
        Assert.Equal("application/cloudevents+json", delivered.BasicProperties.ContentType);
        Assert.True(delivered.BasicProperties.Persistent);
        var headers = delivered.BasicProperties.Headers!;
        Assert.DoesNotContain("baggage", headers.Keys);
        Assert.DoesNotContain("tracestate", headers.Keys);
        var traceParent = Encoding.ASCII.GetString(Assert.IsType<byte[]>(headers["traceparent"]));
        Assert.True(ActivityContext.TryParse(traceParent, null, out var publishedContext));
        Assert.Equal(parent.TraceId, publishedContext.TraceId);
        Assert.NotEqual(parent.SpanId, publishedContext.SpanId);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(
            Guid.NewGuid(), "profile.updated.v1", body));
        Assert.Null(await channel.BasicGetAsync(queue, autoAck: true));
    }

    [Fact]
    public async Task Consumer_uses_valid_traceparent_and_ignores_malformed_trace_context()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = TraceListener(stopped);
        await using var container = new RabbitMqBuilder($"{RabbitRepository}:{ImageRelease}").Build();
        await container.StartAsync();
        var brokerUri = new Uri(container.GetConnectionString());
        var bus = new RabbitMqEventBus(brokerUri);
        var valid = new TaskCompletionSource<(ActivityContext Context, ActivitySpanId ParentSpanId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var malformed = new TaskCompletionSource<(ActivityContext Context, ActivitySpanId ParentSpanId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var missing = new TaskCompletionSource<(ActivityContext Context, ActivitySpanId ParentSpanId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await bus.SubscribeAsync("trace-test",
            new Dictionary<string, PlatformEventHandler>
            {
                [EventType] = (body, _) =>
                {
                    var activity = Activity.Current!;
                    var observed = (activity.Context, activity.ParentSpanId);
                    if (body.Span.SequenceEqual("valid"u8)) valid.TrySetResult(observed);
                    if (body.Span.SequenceEqual("malformed"u8)) malformed.TrySetResult(observed);
                    if (body.Span.SequenceEqual("missing"u8)) missing.TrySetResult(observed);
                    return Task.CompletedTask;
                }
            });
        await using var publisher = await bus.CreatePublisherAsync();
        using var parent = new Activity("api-request").Start();
        await publisher.PublishAsync(Guid.NewGuid(), EventType, "valid"u8.ToArray());
        var consumed = await valid.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var producer = Assert.Single(stopped, activity =>
            activity.DisplayName == "messaging.publish" && activity.TraceId == parent.TraceId);
        Assert.Equal(parent.TraceId, consumed.Context.TraceId);
        Assert.Equal(producer.SpanId, consumed.ParentSpanId);

        parent.Stop();
        await using var connection = await new ConnectionFactory { Uri = brokerUri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync("excellore.events", EventType, mandatory: false,
            new BasicProperties
            {
                Persistent = true,
                Headers = new Dictionary<string, object?>
                {
                    ["traceparent"] = Encoding.ASCII.GetBytes("invalid"),
                    ["baggage"] = Encoding.ASCII.GetBytes("must-not-propagate")
                }
            }, "malformed"u8.ToArray());
        var independent = await malformed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(parent.TraceId, independent.Context.TraceId);
        Assert.Equal(default, independent.ParentSpanId);

        await channel.BasicPublishAsync("excellore.events", EventType, mandatory: false,
            new BasicProperties { Persistent = true }, "missing"u8.ToArray());
        var withoutHeader = await missing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(parent.TraceId, withoutHeader.Context.TraceId);
        Assert.Equal(default, withoutHeader.ParentSpanId);
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

    private static ActivityListener TraceListener(ConcurrentQueue<Activity>? stopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ExcelloreTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped?.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
