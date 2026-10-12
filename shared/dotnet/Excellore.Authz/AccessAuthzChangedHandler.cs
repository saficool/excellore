using Excellore.Contracts;

namespace Excellore.Authz;

/// <summary>Transport-neutral cache invalidation for an Access authorization-change event.</summary>
public sealed class AccessAuthzChangedHandler(IAuthzCacheInvalidator invalidator)
{
    private const int MaxRememberedEvents = 10_000;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _handled = [];
    private readonly Queue<Guid> _order = new();

    public void Handle(ReadOnlySpan<byte> eventJson)
    {
        var envelope = AccessAuthzChangedV1.Parse(eventJson);
        lock (_sync)
        {
            if (_handled.Contains(envelope.Id))
            {
                return;
            }

            // Mark the event only after invalidation succeeds, so failed deliveries can retry.
            invalidator.Invalidate(envelope.TenantId, envelope.Data!.UserIds);
            _handled.Add(envelope.Id);
            _order.Enqueue(envelope.Id);
            if (_order.Count > MaxRememberedEvents)
            {
                _handled.Remove(_order.Dequeue());
            }
        }
    }
}
