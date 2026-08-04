using SonicRelay.Windows.ApiClient.Settings;
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
    private readonly IRelaySettingsApiClient? relaySettingsApi;
    private AudioQualityProfile selectedProfile = AudioQualityProfile.Default;
    private string backendUrlInput = "";
    private string? backendUrlError;
    private string relayMode = RelayModes.Automatic;
    private string turnUriInput = "";
    private string? relaySettingsError;
    private bool hasDeviceIdentity;

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

    /// <summary>
    /// Connected overload that also wires the relay-mode/coturn settings surface (issue #26
    /// follow-up — the backend's /api/settings/relay is now the source of truth for
    /// <see cref="RelayMode"/>; this overload gives the view model a client to read and write
    /// through it).
    /// </summary>
    public SettingsViewModel(
        string backendUrl,
        RelayPreferenceStore relay,
        AudioQualityStore quality,
        IRelaySettingsApiClient relaySettingsApi,
        Func<string, Task<string?>> changeBackendUrl)
        : this(backendUrl, relay, quality, changeBackendUrl)
    {
        this.relaySettingsApi = relaySettingsApi ?? throw new ArgumentNullException(nameof(relaySettingsApi));
        relayMode = relay.RelayMode;
        RefreshRelaySettingsCommand = new RelayCommand(RefreshRelaySettingsAsync);
        SaveRelayModeCommand = new RelayCommand(SaveRelayModeAsync);
        SaveTurnUriCommand = new RelayCommand(SaveTurnUriAsync);
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

    public IReadOnlyList<string> RelayModeOptions { get; } =
    [
        RelayModes.Automatic,
        RelayModes.ForceRelay,
        RelayModes.DisableFallback,
    ];

    /// <summary>The relay policy the backend hands out to every device; one of
    /// <see cref="RelayModeOptions"/>. Bound to the settings ComboBox — call
    /// <see cref="SaveRelayModeAsync"/> to write a change through to the server.</summary>
    public string RelayMode
    {
        get => relayMode;
        set => SetProperty(ref relayMode, value);
    }

    /// <summary>The coturn URI field; bound to the settings TextBox — call
    /// <see cref="SaveTurnUriAsync"/> to write a change through to the server.</summary>
    public string TurnUriInput
    {
        get => turnUriInput;
        set => SetProperty(ref turnUriInput, value);
    }

    /// <summary>Error from the last relay-settings fetch/save, or null once it succeeds.</summary>
    public string? RelaySettingsError
    {
        get => relaySettingsError;
        private set => SetProperty(ref relaySettingsError, value);
    }

    /// <summary>Whether the device has bootstrapped an identity yet; the coturn URL field is
    /// hidden until then, since it authenticates against the backend as that device.</summary>
    public bool HasDeviceIdentity
    {
        get => hasDeviceIdentity;
        private set => SetProperty(ref hasDeviceIdentity, value);
    }

    /// <summary>Called by <see cref="MainWindowViewModel.Apply"/> whenever the attached
    /// runtime's snapshot changes, to keep <see cref="HasDeviceIdentity"/> current.</summary>
    public void UpdateAuthentication(bool value) => HasDeviceIdentity = value;

    /// <summary>No-op until the connected overload replaces it; mirrors <see cref="SaveBackendUrlCommand"/>.</summary>
    public RelayCommand RefreshRelaySettingsCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>No-op until the connected overload replaces it; mirrors <see cref="SaveBackendUrlCommand"/>.</summary>
    public RelayCommand SaveRelayModeCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>No-op until the connected overload replaces it; mirrors <see cref="SaveBackendUrlCommand"/>.</summary>
    public RelayCommand SaveTurnUriCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>Fetches the server's current relay settings and applies them locally.</summary>
    public async Task RefreshRelaySettingsAsync()
    {
        if (relaySettingsApi is null) return;
        try
        {
            ApplyRelaySettings(await relaySettingsApi.GetAsync());
            RelaySettingsError = null;
        }
        catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
        {
            RelaySettingsError = exception.Message;
        }
    }

    /// <summary>Writes <see cref="RelayMode"/> through to the server and applies its response.</summary>
    public async Task SaveRelayModeAsync()
    {
        if (relaySettingsApi is null) return;
        try
        {
            ApplyRelaySettings(await relaySettingsApi.UpdateAsync(new UpdateRelaySettingsRequest(relayMode, null, null)));
            RelaySettingsError = null;
        }
        catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
        {
            RelaySettingsError = exception.Message;
        }
    }

    /// <summary>Writes <see cref="TurnUriInput"/> through to the server (as a single-element
    /// list, or an empty list if blank) and applies its response.</summary>
    public async Task SaveTurnUriAsync()
    {
        if (relaySettingsApi is null) return;
        try
        {
            var uris = string.IsNullOrWhiteSpace(turnUriInput) ? Array.Empty<string>() : new[] { turnUriInput };
            ApplyRelaySettings(await relaySettingsApi.UpdateAsync(new UpdateRelaySettingsRequest(null, uris, null)));
            RelaySettingsError = null;
        }
        catch (SonicRelay.Windows.ApiClient.Errors.ApiClientException exception)
        {
            RelaySettingsError = exception.Message;
        }
    }

    private void ApplyRelaySettings(RelaySettingsResponse response)
    {
        RelayMode = response.RelayMode;
        TurnUriInput = response.TurnUris.Count > 0 ? response.TurnUris[0] : "";
        if (relay is not null)
        {
            Persist(relay.ApplyFetchedRelayModeAsync(response.RelayMode));
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
