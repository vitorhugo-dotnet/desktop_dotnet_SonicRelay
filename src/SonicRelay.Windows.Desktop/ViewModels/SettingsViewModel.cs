using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Settings surface (issue #32): backend endpoint, the force-relay ICE preference and the Opus
/// quality profile. It edits the same user-scoped preference stores the WebRTC factory reads,
/// so a change applies to the next stream — mirroring the WinUI Settings page. Without an
/// attached runtime it is <see cref="IsConnected"/> = false and read-only.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly RelayPreferenceStore? relay;
    private readonly AudioQualityStore? quality;
    private bool forceRelay;
    private AudioQualityProfile selectedProfile = AudioQualityProfile.Default;

    /// <summary>Disconnected state — no backend/runtime attached.</summary>
    public SettingsViewModel()
    {
    }

    public SettingsViewModel(string backendUrl, RelayPreferenceStore relay, AudioQualityStore quality)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(quality);
        this.relay = relay;
        this.quality = quality;
        IsConnected = true;
        BackendUrl = string.IsNullOrWhiteSpace(backendUrl) ? "—" : backendUrl;
        forceRelay = relay.ForceRelay;
        selectedProfile = ResolveProfile(quality.CurrentProfile);
    }

    public bool IsConnected { get; }
    public string BackendUrl { get; } = "—";
    public IReadOnlyList<AudioQualityProfile> Profiles { get; } = AudioQualityProfile.Presets;

    /// <summary>Restrict ICE to relay (TURN) candidates; persisted immediately.</summary>
    public bool ForceRelay
    {
        get => forceRelay;
        set
        {
            if (SetProperty(ref forceRelay, value) && relay is not null)
                Persist(relay.SetForceRelayAsync(value));
        }
    }

    public AudioQualityProfile SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (value is not null && SetProperty(ref selectedProfile, value) && quality is not null)
                Persist(quality.SetProfileAsync(value));
        }
    }

    // The stored profile may be a deserialized copy or a custom profile; bind the matching
    // preset instance so the selector reflects it, defaulting to the app default otherwise.
    private AudioQualityProfile ResolveProfile(AudioQualityProfile current) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase))
        ?? AudioQualityProfile.Default;

    private static async void Persist(Task write)
    {
        try
        {
            await write;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Persisting a preference is best-effort; the in-memory value already applies to
            // the next stream, so a failed disk write must not crash the UI.
        }
    }
}
