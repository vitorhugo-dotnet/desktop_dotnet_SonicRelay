using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Settings surface (issue #32): backend endpoint, the relay mode/coturn override and the Opus
/// quality profile. It edits the same user-scoped preference stores the WebRTC factory reads,
/// so a change applies to the next stream — mirroring the WinUI Settings page. Without an
/// attached runtime it is <see cref="IsConnected"/> = false and read-only.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly RelayPreferenceStore? relay;
    private readonly AudioQualityStore? quality;
    private readonly Func<string, Task<string?>>? changeBackendUrl;
    private readonly IRelaySettingsApiClient? relaySync;
    private bool refreshedFromServer;
    private AudioQualityProfile selectedProfile = AudioQualityProfile.Default;
    private string backendUrlInput = "";
    private string? backendUrlError;
    private string relayMode = RelayModes.Automatic;
    private string turnUriInput = "";
    private bool hasDeviceIdentity;

    /// <summary>Disconnected state — no backend/runtime attached.</summary>
    public SettingsViewModel()
    {
    }

    /// <summary>
    /// Connected overload: relay mode and the coturn override (issue #26 follow-up — both are
    /// per-device local preferences read and written straight through
    /// <see cref="RelayPreferenceStore"/>, not synced through a server) alongside the audio
    /// quality profile. <see cref="TurnUriInput"/> starts from the store's own
    /// <see cref="RelayPreferenceStore.CoturnUrlOverride"/> — the user's own prior override, if
    /// any — never a value fetched from a backend.
    /// </summary>
    public SettingsViewModel(string backendUrl, RelayPreferenceStore relay, AudioQualityStore quality,
        IRelaySettingsApiClient? relaySync = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(quality);
        this.relay = relay;
        this.relaySync = relaySync;
        this.quality = quality;
        IsConnected = true;
        BackendUrl = string.IsNullOrWhiteSpace(backendUrl) ? "—" : backendUrl;
        selectedProfile = ResolveProfile(quality.CurrentProfile);
        relayMode = relay.RelayMode;
        turnUriInput = relay.CoturnUrlOverride ?? "";
        SaveRelayModeCommand = new RelayCommand(SaveRelayModeAsync);
        // No longer gated on a server-fetched-flag (issue #26 follow-up): there is no server
        // value a blank, unfetched field could silently wipe, since nothing is fetched.
        // HasDeviceIdentity stays — the coturn URL authenticates against the backend as this
        // device.
        SaveTurnUriCommand = new RelayCommand(SaveTurnUriAsync, () => HasDeviceIdentity);
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
        Func<string, Task<string?>> changeBackendUrl,
        IRelaySettingsApiClient? relaySync = null)
        : this(backendUrl, relay, quality, relaySync)
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
    /// or null once a save succeeds. Bound via <c>Converter={x:Static ObjectConverters.IsNotNull}</c>
    /// in XAML rather than a bindable bool wrapper.</summary>
    public string? BackendUrlError
    {
        get => backendUrlError;
        private set => SetProperty(ref backendUrlError, value);
    }

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

    /// <summary>This device's relay policy; one of <see cref="RelayModeOptions"/>. Bound to the
    /// settings ComboBox — call <see cref="SaveRelayModeAsync"/> to persist a change.</summary>
    public string RelayMode
    {
        get => relayMode;
        set => SetProperty(ref relayMode, value);
    }

    /// <summary>The coturn URI field; bound to the settings TextBox — call
    /// <see cref="SaveTurnUriAsync"/> to persist a change. Blank means "no override", i.e. use
    /// whatever the backend hands out.</summary>
    public string TurnUriInput
    {
        get => turnUriInput;
        set => SetProperty(ref turnUriInput, value);
    }

    /// <summary>Whether the device has bootstrapped an identity yet; the coturn URL field is
    /// hidden until then, since it authenticates against the backend as that device.</summary>
    public bool HasDeviceIdentity
    {
        get => hasDeviceIdentity;
        private set => SetProperty(ref hasDeviceIdentity, value);
    }

    /// <summary>
    /// Called by <see cref="MainWindowViewModel.Apply"/> whenever the attached runtime's
    /// snapshot changes, to keep <see cref="HasDeviceIdentity"/> current (and, with it,
    /// <see cref="SaveTurnUriCommand"/>'s canExecute).
    /// </summary>
    public void UpdateAuthentication(bool value)
    {
        HasDeviceIdentity = value;
        SaveTurnUriCommand.RaiseCanExecuteChanged();
        if (value && !refreshedFromServer)
        {
            refreshedFromServer = true;
            _ = RefreshFromServerAsync();
        }
    }

    /// <summary>
    /// Pulls the effective relay preferences from the backend (which resolves them across this
    /// device's active pairings, latest write wins) and applies them locally — this is how a
    /// coturn override saved on the paired phone shows up here. Best-effort: an unreachable or
    /// older backend just leaves the local values in place.
    /// </summary>
    public async Task RefreshFromServerAsync()
    {
        if (relaySync is null || relay is null) return;
        try
        {
            var settings = await relaySync.GetRelaySettingsAsync();
            RelayMode = ResolveMode(settings.RelayMode);
            TurnUriInput = settings.TurnUris.FirstOrDefault() ?? "";
            await relay.SetRelayModeAsync(RelayMode);
            await relay.SetCoturnUrlOverrideAsync(TurnUriInput);
        }
        catch (Exception)
        {
            // Sync is strictly best-effort; local preferences keep working offline.
        }
    }

    private string ResolveMode(string mode) =>
        RelayModeOptions.FirstOrDefault(option => string.Equals(option, mode, StringComparison.OrdinalIgnoreCase))
        ?? RelayModes.Automatic;

    /// <summary>
    /// Pushes a preference change to the backend so it reaches this device's paired peers.
    /// Best-effort like <see cref="Persist"/>: saving must keep working against an unreachable
    /// or older backend, where the local store alone still applies to the next stream.
    /// </summary>
    private async void SyncToServer(UpdateRelaySettingsRequest request)
    {
        if (relaySync is null || !HasDeviceIdentity) return;
        try
        {
            await relaySync.UpdateRelaySettingsAsync(request);
        }
        catch (Exception)
        {
            // Async void must never throw; the local save already succeeded.
        }
    }

    /// <summary>No-op until the connected overload replaces it; mirrors <see cref="SaveBackendUrlCommand"/>.</summary>
    public RelayCommand SaveRelayModeCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>No-op until the connected overload replaces it; mirrors <see cref="SaveBackendUrlCommand"/>.</summary>
    public RelayCommand SaveTurnUriCommand { get; } = new(() => Task.CompletedTask);

    /// <summary>Persists <see cref="RelayMode"/> to the local preference store. Purely local —
    /// there is nothing to write through to a server. Routed through <see cref="Persist"/>, like
    /// every other preference write in this view model: <see cref="RelayCommand.Execute"/> is
    /// async void with no catch of its own, so an unhandled write failure (a locked preferences
    /// file, a full disk, an unexpected invalid mode) would otherwise take the whole process
    /// down instead of just failing this one save.</summary>
    public Task SaveRelayModeAsync()
    {
        if (relay is not null) Persist(relay.SetRelayModeAsync(relayMode));
        SyncToServer(new UpdateRelaySettingsRequest(RelayMode: relayMode));
        return Task.CompletedTask;
    }

    /// <summary>Persists <see cref="TurnUriInput"/> to the local preference store; a blank value
    /// clears the override. Purely local — see <see cref="SaveRelayModeAsync"/> for why this
    /// goes through <see cref="Persist"/> too.</summary>
    public Task SaveTurnUriAsync()
    {
        if (relay is not null) Persist(relay.SetCoturnUrlOverrideAsync(turnUriInput));
        SyncToServer(new UpdateRelaySettingsRequest(
            TurnUris: string.IsNullOrWhiteSpace(turnUriInput) ? [] : [turnUriInput.Trim()]));
        return Task.CompletedTask;
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
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            // Persisting a preference is best-effort; the in-memory value already applies to
            // the next stream, so a failed disk write (or, for relay mode, an unexpected
            // ArgumentException from RelayPreferenceStore.PersistAsync's own validation) must
            // not crash the UI. RelayCommand.Execute is async void with no catch of its own, so
            // anything that escapes here would otherwise take the whole process down.
        }
    }
}
