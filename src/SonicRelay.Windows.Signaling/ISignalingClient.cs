namespace SonicRelay.Windows.Signaling;

public interface ISignalingClient : IAsyncDisposable
{
    SignalingConnectionState State { get; }
    event Action<SignalingConnectionState>? StateChanged;
    event Action<int>? ReconnectAttempting;
    event Action<SignalingCloseReason>? Closed;

    Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
