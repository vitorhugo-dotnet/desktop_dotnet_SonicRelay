using SonicRelay.Windows.ApiClient.Sessions;
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

    /// <summary>
    /// The mode the active session was created with. Fixed for the session's life, so every
    /// two-way control keys off it rather than off anything a peer says.
    /// </summary>
    public string SessionMode { get; init; } = SessionModes.Broadcast;

    /// <summary>Whether this device is currently withholding its outgoing audio.</summary>
    public bool OutgoingAudioMuted { get; init; }

    /// <summary>Playback state for audio arriving from participants (two-way sessions only).</summary>
    public AudioPlaybackState PlaybackState { get; init; } = AudioPlaybackState.Stopped;

    /// <summary>The endpoint incoming audio is played on, once one has been opened.</summary>
    public AudioDeviceInfo? PlaybackDevice { get; init; }

    /// <summary>
    /// The backend's authoritative state for every participant seen in this session, this
    /// device included. The only place publish permission is read from.
    /// </summary>
    public IReadOnlyList<ParticipantAudioState> Participants { get; init; } = [];

    public bool HasDeviceIdentity => IsAuthenticated && DeviceId.HasValue;
    public bool CanCreateSession => HasDeviceIdentity && SessionId is null && !IsBusy;
    public bool CanStartAudio => SessionId.HasValue && SignalingState == SignalingConnectionState.Connected
        && AudioState is AudioCaptureState.Stopped or AudioCaptureState.Faulted && !IsBusy;
    public bool CanStopAudio => AudioState is AudioCaptureState.Capturing or AudioCaptureState.Paused
        or AudioCaptureState.Recovering or AudioCaptureState.Faulted;
    public bool CanEndSession => SessionId.HasValue && !IsBusy;

    public bool IsDuplexSession => SessionModes.IsDuplex(SessionMode);

    /// <summary>
    /// Whether incoming audio is being played onto the very endpoint this device is capturing.
    ///
    /// That is a feedback loop, not a subtle quality problem: what the other side sends is
    /// played, captured by the loopback, and sent straight back to them. It cannot be fixed
    /// from inside the app — the capture endpoint and the playback endpoint have to differ —
    /// so it is surfaced rather than worked around.
    /// </summary>
    public bool PlaysIntoCapturedOutput =>
        IsDuplexSession
        && PlaybackDevice is { Id.Length: > 0 }
        && AudioDiagnostics?.Device is { Id.Length: > 0 } captured
        && string.Equals(captured.Id, PlaybackDevice.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the mute control applies at all: only a live two-way session has one.</summary>
    public bool CanToggleOutgoingAudio => IsDuplexSession && SessionId.HasValue && !IsBusy;

    /// <summary>Participants other than this device, which is the list the publisher acts on.</summary>
    public IReadOnlyList<ParticipantAudioState> OtherParticipants =>
        Participants.Where(participant => !participant.IsPublisher).ToArray();
}
