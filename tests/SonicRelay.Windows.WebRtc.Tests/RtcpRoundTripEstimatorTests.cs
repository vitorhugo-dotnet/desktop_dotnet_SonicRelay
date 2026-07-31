using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.WebRtc.Tests;

/// <summary>
/// RTT is derived from local elapsed time minus the receiver's reported hold (DLSR), and must
/// fail safe (null) for non-positive or implausible results so the UI shows "—" (issue #32).
/// </summary>
public sealed class RtcpRoundTripEstimatorTests
{
    private static readonly DateTime Sent = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compact_ntp_takes_the_middle_32_bits()
    {
        // 0x0001_2345_6789_ABCD -> middle 32 bits are 0x2345_6789.
        Assert.Equal(0x2345_6789u, RtcpRoundTripEstimator.CompactNtp(0x0001_2345_6789_ABCDul));
    }

    [Fact]
    public void Rtt_is_elapsed_minus_receiver_hold()
    {
        // Sent 100 ms ago; receiver held 20 ms (20 ms == 0.02 * 65536 DLSR units).
        var now = Sent + TimeSpan.FromMilliseconds(100);
        var dlsr = (uint)Math.Round(0.02 * 65536);

        var rtt = RtcpRoundTripEstimator.EstimateRoundTripTime(Sent, now, dlsr);

        Assert.NotNull(rtt);
        Assert.Equal(80, rtt!.Value.TotalMilliseconds, 0);
    }

    [Fact]
    public void Non_positive_result_is_rejected()
    {
        // Receiver hold exceeds the elapsed time (stale/mismatched report).
        var now = Sent + TimeSpan.FromMilliseconds(10);
        var dlsr = (uint)Math.Round(0.05 * 65536);

        Assert.Null(RtcpRoundTripEstimator.EstimateRoundTripTime(Sent, now, dlsr));
    }

    [Fact]
    public void Implausibly_large_result_is_rejected()
    {
        var now = Sent + TimeSpan.FromSeconds(30);

        Assert.Null(RtcpRoundTripEstimator.EstimateRoundTripTime(Sent, now, dlsrUnits: 0));
    }
}
