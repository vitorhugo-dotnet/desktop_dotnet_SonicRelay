using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// One participant row in a two-way session, with the publisher-only control that grants or
/// revokes its permission to transmit its audio.
///
/// Everything shown here comes from the backend's own broadcasts — never from what the peer
/// claims about itself — because the API is the only authority on who may publish, and it
/// cannot enforce it in the media path.
/// </summary>
public sealed class TwoWayParticipantViewModel : ViewModelBase
{
    private readonly Func<Guid, bool, Task> setPermission;

    public TwoWayParticipantViewModel(ParticipantAudioState state, Func<Guid, bool, Task> setPermission)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.setPermission = setPermission ?? throw new ArgumentNullException(nameof(setPermission));
        State = state;
        // A participant id that is not a GUID is a backend this build does not understand;
        // the row still renders, it just carries no permission control to act with.
        Id = Guid.TryParse(state.ParticipantId, out var id) ? id : Guid.Empty;
        TogglePermissionCommand = new RelayCommand(TogglePermissionAsync, () => Id != Guid.Empty);
    }

    public ParticipantAudioState State { get; }
    public Guid Id { get; }
    public RelayCommand TogglePermissionCommand { get; }

    /// <summary>A short, stable label; participant ids are long and the full value adds nothing.</summary>
    public string DisplayName =>
        State.IsPublisher ? "This device" : $"Participant {State.ParticipantId[..Math.Min(8, State.ParticipantId.Length)]}";

    public string Status => State switch
    {
        { AudioSendAllowed: false } => "Listening only",
        { AudioMuted: true } => "Not sending audio",
        { CanSendAudio: true } => "Sending audio",
        _ => "Allowed to send audio",
    };

    public bool CanTalk => State.AudioSendAllowed;

    /// <summary>The publisher never revokes its own permission, so its row has no control.</summary>
    public bool ShowsPermissionControl => !State.IsPublisher && Id != Guid.Empty;

    public string PermissionActionLabel =>
        State.AudioSendAllowed ? "Stop accepting audio" : "Accept audio";

    private Task TogglePermissionAsync() => setPermission(Id, !State.AudioSendAllowed);
}
