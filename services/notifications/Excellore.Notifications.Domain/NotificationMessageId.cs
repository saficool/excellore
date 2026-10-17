namespace Excellore.Notifications.Domain;

public static class NotificationMessageId
{
    private const string Prefix = "excellore-notification-";

    public static string Create(Guid correlationId, string? domain)
    {
        if (correlationId == Guid.Empty || !IsValidDomain(domain))
            throw new InvalidOperationException("Notification Message-ID configuration is invalid.");
        return $"{Prefix}{correlationId:N}@{domain!.ToLowerInvariant()}";
    }

    public static bool TryParse(string? value, string? expectedDomain, out Guid correlationId)
    {
        correlationId = Guid.Empty;
        if (!IsValidDomain(expectedDomain) || string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.Length >= 2 && candidate[0] == '<' && candidate[^1] == '>')
            candidate = candidate[1..^1];
        var suffix = $"@{expectedDomain!.ToLowerInvariant()}";
        if (!candidate.StartsWith(Prefix, StringComparison.Ordinal) ||
            !candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var raw = candidate[Prefix.Length..^suffix.Length];
        return raw.Length == 32 && Guid.TryParseExact(raw, "N", out correlationId) &&
               correlationId != Guid.Empty;
    }

    private static bool IsValidDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253) return false;
        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }
}
