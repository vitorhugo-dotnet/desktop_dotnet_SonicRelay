using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.WebRtc.Tests;

/// <summary>
/// The RTCP reception-report projection must convert raw report fields into the units the
/// dashboard shows (issue #32): RTP jitter units on the audio clock become a duration, and the
/// 8-bit fraction-lost becomes a percentage.
/// </summary>
public sealed class AudioReceptionDiagnosticsTests
{
    [Fact]
    public void Jitter_units_are_converted_to_time_on_the_audio_clock()
    {
        // 480 RTP units at 48 kHz == 10 ms.
        var reception = AudioReceptionDiagnostics.FromReport(
            jitterRtpUnits: 480, fractionLost: 0, cumulativePacketsLost: 0, clockRateHz: 48000);

        Assert.Equal(10d, reception.Jitter.TotalMilliseconds, 3);
    }

    [Fact]
    public void Fraction_lost_is_scaled_to_a_percentage()
    {
        // RTCP fraction lost is fixed-point out of 256; 128 == 50 %.
        var reception = AudioReceptionDiagnostics.FromReport(
            jitterRtpUnits: 0, fractionLost: 128, cumulativePacketsLost: 7, clockRateHz: 48000);

        Assert.Equal(50d, reception.PacketLossPercent, 3);
        Assert.Equal(7, reception.CumulativePacketsLost);
    }

    [Fact]
    public void A_non_positive_clock_rate_falls_back_to_48_khz()
    {
        var reception = AudioReceptionDiagnostics.FromReport(
            jitterRtpUnits: 48, fractionLost: 0, cumulativePacketsLost: 0, clockRateHz: 0);

        Assert.Equal(1d, reception.Jitter.TotalMilliseconds, 3);
    }
}
