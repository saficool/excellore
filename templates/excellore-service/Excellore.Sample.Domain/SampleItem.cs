using Excellore.Platform;

namespace Excellore.Sample.Domain;

// Class T. Persist only opaque, non-personal sample data.
public sealed class SampleItem : ITenantOwnedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
}
