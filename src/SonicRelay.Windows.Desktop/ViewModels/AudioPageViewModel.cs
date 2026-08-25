using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Audio surface (issue #32): picks which system output endpoint to capture, and — for
/// dotnet_SonicRelay#22 — hosts the two-way audio controls: opening the next session for
/// conversation, muting, and the publisher-only permission control over who may talk.
///
/// The two-way half is only offered when the platform composition actually supplied both a
/// microphone and a playback device. Offering it otherwise would let a user start a
/// conversation this build could only half hold up. Without an attached runtime the page is
/// <see cref="IsConnected"/> = false and read-only.
/// </summary>
public sealed class AudioPageViewModel : ViewModelBase
{
    /// <summary>Sentinel id for the "system default" entry (null device id to the enumerator).</summary>
    public const string SystemDefaultId = "";

    private readonly IAudioDeviceEnumerator? enumerator;
    private readonly AudioOutputPreferenceStore? store;
    private readonly PublisherWorkflow? workflow;
    private AudioOutputDevice? selectedDevice;
    private bool startNextSessionAsTwoWay;
    private PublisherSnapshot? snapshot;

    /// <summary>Disconnected state — no runtime attached.</summary>
    public AudioPageViewModel()
    {
        Devices = [];
    }

    public AudioPageViewModel(
        IAudioDeviceEnumerator enumerator,
        AudioOutputPreferenceStore store,
        PublisherWorkflow? workflow = null)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(store);
        this.enumerator = enumerator;
        this.store = store;
        this.workflow = workflow;
        IsConnected = true;
        ToggleMuteCommand = new RelayCommand(ToggleMuteAsync, () => snapshot?.CanToggleOutgoingAudio == true);

        var devices = new List<AudioOutputDevice>
        {
            new(SystemDefaultId, "System default", IsDefault: true),
        };
        devices.AddRange(enumerator.GetOutputDevices());
        Devices = devices;

        var preferred = enumerator.PreferredDeviceId ?? SystemDefaultId;
        selectedDevice = devices.FirstOrDefault(device => device.Id == preferred) ?? devices[0];
    }

    public bool IsConnected { get; }
    public IReadOnlyList<AudioOutputDevice> Devices { get; }

    /// <summary>Whether this build can capture a microphone and play remote audio back.</summary>
    public bool SupportsTwoWayAudio => workflow?.SupportsTwoWayAudio == true;

    public RelayCommand ToggleMuteCommand { get; } = new(() => Task.CompletedTask, () => false);

    /// <summary>
    /// Whether the next session should be created for conversation. It applies at creation
    /// because the backend fixes a session's mode there and never changes it — and a peer
    /// connection's audio direction is fixed with it.
    /// </summary>
    public bool StartNextSessionAsTwoWay
    {
        get => startNextSessionAsTwoWay;
        set => SetProperty(ref startNextSessionAsTwoWay, value);
    }

    public bool IsTwoWaySession => snapshot?.IsDuplexSession == true;

    public bool IsMuted => snapshot?.OutgoingAudioMuted == true;

    public string MuteActionLabel => IsMuted ? "Unmute microphone" : "Mute microphone";

    public string PlaybackStatus => snapshot?.PlaybackState switch
    {
        AudioPlaybackState.Playing => "Playing audio from the other participants",
        AudioPlaybackState.Starting => "Opening the playback device…",
        AudioPlaybackState.Faulted => "The playback device is unavailable",
        _ => "No incoming audio yet",
    };

    public IReadOnlyList<TwoWayParticipantViewModel> Participants { get; private set; } = [];

    /// <summary>Folds the latest publisher snapshot in, so the two-way controls follow the session.</summary>
    public void Update(PublisherSnapshot? state)
    {
        snapshot = state;
        Participants = state is null
            ? []
            : state.Participants.Select(participant =>
                new TwoWayParticipantViewModel(participant, SetPermissionAsync)).ToArray();
        RaisePropertyChanged(nameof(IsTwoWaySession));
        RaisePropertyChanged(nameof(IsMuted));
        RaisePropertyChanged(nameof(MuteActionLabel));
        RaisePropertyChanged(nameof(PlaybackStatus));
        RaisePropertyChanged(nameof(Participants));
        ToggleMuteCommand.RaiseCanExecuteChanged();
    }

    private Task ToggleMuteAsync() =>
        workflow is null ? Task.CompletedTask : workflow.SetOutgoingAudioMutedAsync(!IsMuted);

    private Task SetPermissionAsync(Guid participantId, bool canSendAudio) =>
        workflow is null
            ? Task.CompletedTask
            : workflow.SetParticipantAudioPermissionAsync(participantId, canSendAudio);

    public AudioOutputDevice? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (value is null || !SetProperty(ref selectedDevice, value) || enumerator is null || store is null)
                return;
            var deviceId = value.Id == SystemDefaultId ? null : value.Id;
            enumerator.SelectOutputDevice(deviceId);
            Persist(store.SetSelectedDeviceAsync(deviceId, deviceId is null ? null : value.Name));
        }
    }

    private static async void Persist(Task write)
    {
        try
        {
            await write;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Best-effort persistence; the enumerator already has the selection for the next start.
        }
    }
}
