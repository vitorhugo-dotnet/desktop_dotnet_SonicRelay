namespace SonicRelay.Windows.WebRtc;

/// <summary>
/// Pure round-trip-time estimation from RTCP sender/receiver report correlation (issue #32).
/// Rather than reconstructing NTP wall-clock arithmetic, the publisher records when it sent
/// each sender report (SR) and, when a receiver report (RR) echoes that SR's compact NTP
/// timestamp (LSR), derives RTT from the local elapsed time minus the receiver's reported
/// hold time (DLSR). This keeps the maths epoch-free and unit-testable, and it fails safe:
/// an out-of-range result yields <c>null</c> so the UI shows "—" rather than a wrong number.
/// </summary>
public static class RtcpRoundTripEstimator
{
    /// <summary>RTCP's "middle 32 bits" of a 64-bit NTP timestamp (LSR/echoed form).</summary>
    public static uint CompactNtp(ulong ntpTimestamp) => (uint)((ntpTimestamp >> 16) & 0xFFFFFFFF);

    // DLSR (delay since last SR) is expressed in units of 1/65536 seconds.
    private const double DlsrUnitsPerSecond = 65536d;

    // A plausible upper bound; anything larger is treated as a stale/mismatched correlation.
    private static readonly TimeSpan MaxPlausibleRtt = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Estimates RTT as (now − when-we-sent-the-SR) − DLSR, returning null when the result is
    /// non-positive or implausibly large (a stale or mismatched report).
    /// </summary>
    public static TimeSpan? EstimateRoundTripTime(DateTime sentAtUtc, DateTime nowUtc, uint dlsrUnits)
    {
        var elapsed = nowUtc - sentAtUtc;
        var receiverHold = TimeSpan.FromSeconds(dlsrUnits / DlsrUnitsPerSecond);
        var rtt = elapsed - receiverHold;
        return rtt > TimeSpan.Zero && rtt <= MaxPlausibleRtt ? rtt : null;
    }
}
