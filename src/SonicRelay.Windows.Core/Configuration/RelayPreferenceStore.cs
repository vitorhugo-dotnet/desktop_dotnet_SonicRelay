using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Per-device local preferences for the relay mode and an optional coturn URL override
/// (issue #26 follow-up — these used to sync through a backend-owned row that was a single row
/// global to the whole deployment, so one device editing it silently changed the relay for
/// every other device the backend served; that backend endpoint is gone now, and both values
/// are local-only). The WebRTC factory reads <see cref="ForceRelay"/> live via a delegate, and
/// <see cref="SonicRelay.Windows.ApiClient.WebRtc.BackendIceServersProvider"/> reads
/// <see cref="RelayMode"/>/<see cref="CoturnUrlOverride"/> the same way.
/// </summary>
public sealed class RelayPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(UserConfigurationLoader.DefaultDirectory, "preferences.json");

    private readonly string _path;

    public RelayPreferenceStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        RelayMode = Load();
    }

    /// <summary>One of <see cref="RelayModes"/>; never any other value.</summary>
    public string RelayMode { get; private set; }

    /// <summary>Restrict ICE to relay (TURN) candidates; read live by the WebRTC factory.</summary>
    public bool ForceRelay => RelayMode == RelayModes.ForceRelay;

    /// <summary>
    /// A user-supplied TURN URL that replaces the one the backend hands out, or null to use
    /// the backend's. Deliberately never pre-filled with the backend's value: the deployment's
    /// relay host is not disclosed through this UI, and blank means "use whatever the server
    /// sends".
    ///
    /// The TURN credential is signed by the backend as HMAC-SHA1(TURN_STATIC_AUTH_SECRET,
    /// "&lt;expiry&gt;:&lt;deviceId&gt;"), and this override reuses it, so it only authenticates
    /// against a coturn sharing that same static secret — i.e. another host or port of the same
    /// relay deployment, not a third-party TURN server.
    /// </summary>
    public string? CoturnUrlOverride { get; private set; }

    /// <summary>A user changed the mode from Settings; purely local now — nothing to confirm
    /// with a server.</summary>
    public Task SetRelayModeAsync(string mode, CancellationToken cancellationToken = default) =>
        PersistAsync(mode, cancellationToken);

    /// <summary>A user changed the coturn override from Settings; blank/whitespace is stored as
    /// no override (null), which restores the backend's own TURN entries.</summary>
    public Task SetCoturnUrlOverrideAsync(string? url, CancellationToken cancellationToken = default)
    {
        CoturnUrlOverride = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        return PersistAsync(RelayMode, cancellationToken);
    }

    private async Task PersistAsync(string mode, CancellationToken cancellationToken)
    {
        if (!RelayModes.IsValid(mode))
        {
            throw new ArgumentException($"Unknown relay mode '{mode}'.", nameof(mode));
        }

        RelayMode = mode;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(
            _path,
            JsonSerializer.Serialize(new PreferencesDocument(mode, null, CoturnUrlOverride), JsonOptions),
            cancellationToken);
    }

    private string Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                CoturnUrlOverride = null;
                return RelayModes.Automatic;
            }
            var document = JsonSerializer.Deserialize<PreferencesDocument>(File.ReadAllText(_path), JsonOptions);
            CoturnUrlOverride = document?.CoturnUrlOverride;
            if (document?.RelayMode is { } mode && RelayModes.IsValid(mode)) return mode;
            // Migrate the pre-existing boolean-only file shape (issue #26 predecessor).
            if (document?.ForceRelay is true) return RelayModes.ForceRelay;
            return RelayModes.Automatic;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/corrupt preferences file must never block startup; default to automatic.
            CoturnUrlOverride = null;
            return RelayModes.Automatic;
        }
    }

    private sealed record PreferencesDocument(string? RelayMode, bool? ForceRelay, string? CoturnUrlOverride);
}
