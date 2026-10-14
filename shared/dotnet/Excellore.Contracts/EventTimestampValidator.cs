using System.Text.Json;
using System.Text.RegularExpressions;

namespace Excellore.Contracts;

internal static class EventTimestampValidator
{
    public static void RequireOffsetTimestamp(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(timestamp.GetString()!, @"(?:[Zz]|[+-]\d{2}:\d{2})$"))
            throw new InvalidPlatformEventException();
    }
}
