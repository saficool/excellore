using System.Text.Json.Serialization;

namespace Excellore.Contracts;

public sealed record PlatformEvent<TData> where TData : class
{
    [JsonPropertyName("specversion")]
    public string? SpecVersion { get; init; }

    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("tenantid")]
    public Guid TenantId { get; init; }

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonPropertyName("correlationid")]
    public Guid CorrelationId { get; init; }

    [JsonPropertyName("actor")]
    public EventActor? Actor { get; init; }

    [JsonPropertyName("data")]
    public TData? Data { get; init; }
}

public sealed record EventActor
{
    [JsonPropertyName("sub")]
    public string? Sub { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
