using System.Text.Json;

namespace Excellore.Contracts;

internal static class PlatformEventParser
{
    private const int MaxEventBytes = 64 * 1024;

    public static PlatformEvent<TData> Parse<TData>(ReadOnlySpan<byte> json, string eventType,
        string eventSource, bool tenantScoped) where TData : class
    {
        if (json.Length == 0 || json.Length > MaxEventBytes)
        {
            throw new InvalidPlatformEventException();
        }

        PlatformEvent<TData>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PlatformEvent<TData>>(json);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new InvalidPlatformEventException();
        }

        if (envelope is null || envelope.SpecVersion != "1.0" || envelope.Id == Guid.Empty ||
            envelope.Type != eventType || envelope.Source != eventSource ||
            envelope.Time == default ||
            (tenantScoped ? envelope.TenantId is null || envelope.TenantId == Guid.Empty :
                envelope.TenantId is not null) ||
            envelope.CorrelationId == Guid.Empty || envelope.Actor is null ||
            string.IsNullOrWhiteSpace(envelope.Actor.Sub) ||
            envelope.Actor.Type is not ("human" or "machine") || envelope.Data is null)
        {
            throw new InvalidPlatformEventException();
        }

        return envelope;
    }
}

public sealed class InvalidPlatformEventException : Exception
{
    public InvalidPlatformEventException() : base("Invalid platform event.") { }
}
