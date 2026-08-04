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
    private readonly Func<string, Task<string?>>? changeBackendUrl;
    private bool forceRelay;
    private AudioQualityProfile selectedProfile = AudioQualityProfile.Default;
    private string backendUrlInput = "";
    private string? backendUrlError;

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

    /// <summary>
    /// Connected overload that also wires the backend URL editor (issue #26 follow-up): the
    /// always-reachable shell (Task 1) means Settings is now reachable even before the device
    /// has bootstrapped, so an editable backend URL gives the user a way out of a bad
    /// configuration instead of requiring an app restart.
    /// </summary>
    public SettingsViewModel(
        string backendUrl,
        RelayPreferenceStore relay,
        AudioQualityStore quality,
        Func<string, Task<string?>> changeBackendUrl)
        : this(backendUrl, relay, quality)
    {
        this.changeBackendUrl = changeBackendUrl ?? throw new ArgumentNullException(nameof(changeBackendUrl));
        backendUrlInput = BackendUrl == "—" ? "" : BackendUrl;
        SaveBackendUrlCommand = new RelayCommand(SaveBackendUrlAsync);
    }

    public bool IsConnected { get; }
    public string BackendUrl { get; } = "—";
    public IReadOnlyList<AudioQualityProfile> Profiles { get; } = AudioQualityProfile.Presets;

    /// <summary>The editable backend URL field; starts pre-filled with <see cref="BackendUrl"/>.</summary>
    public string BackendUrlInput
    {
        get => backendUrlInput;
        set => SetProperty(ref backendUrlInput, value);
    }

    /// <summary>Validation/save error from the last <see cref="SaveBackendUrlAsync"/> attempt,
    /// or null once a save succeeds.</summary>
    public string? BackendUrlError
    {
        get => backendUrlError;
        private set
        {
            if (SetProperty(ref backendUrlError, value))
                RaisePropertyChanged(nameof(HasBackendUrlError));
        }
    }

    /// <summary>Bindable presence check, following the same pattern as
    /// <see cref="MainWindowViewModel.HasDiagnosticsActionMessage"/> rather than an Avalonia
    /// converter (there is no existing null/bool-to-visibility converter wired up anywhere in
    /// this codebase to reuse).</summary>
    public bool HasBackendUrlError => backendUrlError is not null;

    /// <summary>No-op until the connected overload replaces it; mirrors the always-live
    /// <see cref="RelayCommand"/> fields on <see cref="MainWindowViewModel"/> — never null.</summary>
    public RelayCommand SaveBackendUrlCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>
    /// Validates <see cref="BackendUrlInput"/>, then hands the change off to the delegate
    /// supplied at construction (<see cref="MainWindowViewModel.ChangeBackendUrlAsync"/> in
    /// production) which persists it and reattaches a fresh runtime live.
    /// </summary>
    public async Task SaveBackendUrlAsync()
    {
        if (!Uri.TryCreate(backendUrlInput, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            BackendUrlError = "Enter a valid http:// or https:// URL.";
            return;
        }
        if (changeBackendUrl is null) return;

        BackendUrlError = await changeBackendUrl(uri.AbsoluteUri);
    }

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
