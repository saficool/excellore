using Excellore.Contracts;

namespace Excellore.Authz;

/// <summary>Transport-neutral cache invalidation for an Access authorization-change event.</summary>
public sealed class AccessAuthzChangedHandler(IAuthzCacheInvalidator invalidator)
{
    private const int MaxRememberedEvents = 10_000;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly HashSet<Guid> _handled = [];
    private readonly Queue<Guid> _order = new();

    public async Task HandleAsync(ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken = default)
    {
        var envelope = AccessAuthzChangedV1.Parse(eventJson.Span);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_handled.Contains(envelope.Id))
            {
                return;
            }

            // Mark the event only after invalidation succeeds, so failed deliveries can retry.
            await invalidator.InvalidateAsync(envelope.TenantId!.Value, envelope.Data!.UserIds,
                cancellationToken);
            _handled.Add(envelope.Id);
            _order.Enqueue(envelope.Id);
            if (_order.Count > MaxRememberedEvents)
            {
                _handled.Remove(_order.Dequeue());
            }
        }
        finally
        {
            _sync.Release();
        }
    }
}
