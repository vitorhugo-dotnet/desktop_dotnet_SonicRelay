using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation;

public sealed record PublisherSnapshot
{
    public bool IsAuthenticated { get; init; }
    public Guid? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public Guid? SessionId { get; init; }
    public string? SessionCode { get; init; }
    public int ViewerCount { get; init; }
    public SignalingConnectionState SignalingState { get; init; } = SignalingConnectionState.Disconnected;
    public AudioCaptureState AudioState { get; init; } = AudioCaptureState.Stopped;
    public AudioCaptureDiagnostics? AudioDiagnostics { get; init; }
    public bool IsBusy { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> ActivityLog { get; init; } = [];

    public bool HasDeviceIdentity => IsAuthenticated && DeviceId.HasValue;
    public bool CanCreateSession => HasDeviceIdentity && SessionId is null && !IsBusy;
    public bool CanStartAudio => SessionId.HasValue && SignalingState == SignalingConnectionState.Connected
        && AudioState is AudioCaptureState.Stopped or AudioCaptureState.Faulted && !IsBusy;
    public bool CanStopAudio => AudioState is AudioCaptureState.Capturing or AudioCaptureState.Paused
        or AudioCaptureState.Recovering or AudioCaptureState.Faulted;
    public bool CanEndSession => SessionId.HasValue && !IsBusy;
}
