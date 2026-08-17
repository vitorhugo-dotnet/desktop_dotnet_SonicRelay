namespace SonicRelay.Windows.Signaling;

public enum SignalingConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,

    /// <summary>
    /// The machine has no usable network interface, so recovery is parked rather than retrying.
    /// Distinct from <see cref="Reconnecting"/> on purpose: no attempt budget is being spent
    /// here, and the UI should say "offline" rather than implying the backend is the problem.
    /// </summary>
    WaitingForNetwork,
    Closing,
    Closed,
    Faulted
}
