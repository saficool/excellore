using System.Diagnostics.Metrics;
using System.Net;

namespace Excellore.Authz;

public sealed record TenantProbingOptions(
    int DenialThreshold = 20,
    TimeSpan? Window = null,
    TimeSpan? AlertCooldown = null,
    int MaxTrackedActors = 10_000);

/// <summary>
/// Detects repeated tenant-boundary probes without exporting the user's ID or IP address.
/// Actor keys exist only in process memory; exported metrics contain bounded reason labels only.
/// </summary>
public sealed class TenantProbingDetector
{
    private readonly Dictionary<string, ProbeWindow> windows = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly TenantProbingOptions options;
    private readonly TimeProvider clock;

    public TenantProbingDetector(TenantProbingOptions options, TimeProvider? clock = null)
    {
        if (options.DenialThreshold < 2 || options.Window is { } window && window <= TimeSpan.Zero ||
            options.AlertCooldown is { } cooldown && cooldown <= TimeSpan.Zero ||
            options.MaxTrackedActors < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
    }

    public bool Observe(Guid? userId, IPAddress? remoteAddress)
    {
        var actor = userId is { } id && id != Guid.Empty
            ? $"user:{id:D}"
            : remoteAddress is not null ? $"ip:{remoteAddress}" : null;
        if (actor is null) return false;

        var now = clock.GetUtcNow();
        var windowLength = options.Window ?? TimeSpan.FromMinutes(5);
        var cooldown = options.AlertCooldown ?? TimeSpan.FromMinutes(5);
        lock (sync)
        {
            if (!windows.TryGetValue(actor, out var current))
            {
                if (windows.Count >= options.MaxTrackedActors)
                {
                    foreach (var expired in windows
                        .Where(item => now - item.Value.Start >= windowLength)
                        .Select(item => item.Key).ToArray())
                        windows.Remove(expired);
                }
                if (windows.Count >= options.MaxTrackedActors) return false;
                windows[actor] = new ProbeWindow(now, 1, null);
                return false;
            }

            var next = now - current.Start >= windowLength
                ? new ProbeWindow(now, 1, current.LastAlert)
                : current with { Count = current.Count + 1 };
            var raised = next.Count >= options.DenialThreshold &&
                (next.LastAlert is null || now - next.LastAlert >= cooldown);
            if (raised) next = next with { LastAlert = now };
            windows[actor] = next;
            if (raised) TenantSecurityTelemetry.RecordProbingAlert();
            return raised;
        }
    }

    private sealed record ProbeWindow(DateTimeOffset Start, int Count, DateTimeOffset? LastAlert);
}

public static class TenantSecurityTelemetry
{
    public const string MeterName = "Excellore.Authz";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> TenantGateDenials =
        Meter.CreateCounter<long>("excellore.tenant_gate.denials");
    private static readonly Counter<long> ProbingAlerts =
        Meter.CreateCounter<long>("excellore.security.probing_alerts");
    private static readonly Counter<long> RateLimitRejections =
        Meter.CreateCounter<long>("excellore.rate_limit.rejections");

    internal static void RecordTenantGateDenial(string reason) =>
        TenantGateDenials.Add(1, new KeyValuePair<string, object?>("reason", reason));

    internal static void RecordProbingAlert() => ProbingAlerts.Add(1);

    internal static void RecordRateLimitRejection() => RateLimitRejections.Add(1);
}
