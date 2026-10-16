using System.Diagnostics.Metrics;
using System.Net;

namespace Excellore.Authz.Tests;

public sealed class TenantSecurityTelemetryTests
{
    [Fact]
    public void Repeated_denials_raise_one_anonymous_probing_metric()
    {
        var measurements = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == TenantSecurityTelemetry.MeterName &&
                    instrument.Name == "excellore.security.probing_alerts")
                    current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray())));
        listener.Start();

        var detector = new TenantProbingDetector(
            new TenantProbingOptions(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var actor = Guid.NewGuid();

        Assert.False(detector.Observe(actor, IPAddress.Loopback));
        Assert.False(detector.Observe(actor, IPAddress.Loopback));
        Assert.True(detector.Observe(actor, IPAddress.Loopback));
        Assert.False(detector.Observe(actor, IPAddress.Loopback));

        Assert.Contains(measurements, alert => alert.Value == 1 && alert.Tags.Length == 0);
    }

    [Fact]
    public void Different_actors_do_not_share_a_probe_window()
    {
        var detector = new TenantProbingDetector(
            new TenantProbingOptions(2, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));

        Assert.False(detector.Observe(Guid.NewGuid(), null));
        Assert.False(detector.Observe(Guid.NewGuid(), null));
    }

    [Fact]
    public void Actor_tracking_is_bounded_against_cardinality_attacks()
    {
        var first = Guid.NewGuid();
        var detector = new TenantProbingDetector(
            new TenantProbingOptions(2, TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5), MaxTrackedActors: 2));

        Assert.False(detector.Observe(first, null));
        Assert.False(detector.Observe(Guid.NewGuid(), null));
        Assert.False(detector.Observe(Guid.NewGuid(), null));
        Assert.True(detector.Observe(first, null));
    }
}
