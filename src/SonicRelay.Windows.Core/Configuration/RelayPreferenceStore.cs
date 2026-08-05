using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Last-known-good cache of the server-synced <see cref="RelayMode"/> (issue #26 follow-up —
/// this used to be the sole source of truth for a local-only "force relay" boolean; the real
/// source of truth is now the backend's /api/settings/relay, and this store only exists so
/// the app has something sensible to render before the first fetch completes). The WebRTC
/// factory reads <see cref="ForceRelay"/> live via a delegate, unchanged.
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

    /// <summary>A user changed the mode from Settings; the caller is expected to have already
    /// confirmed this with the server (PUT /api/settings/relay) before calling this — it does
    /// not itself talk to the network.</summary>
    public Task SetRelayModeAsync(string mode, CancellationToken cancellationToken = default) =>
        PersistAsync(mode, cancellationToken);

    /// <summary>A background/opened-Settings/pre-session fetch confirmed the server's current
    /// value; refresh the local cache to match. Same persistence as <see cref="SetRelayModeAsync"/>
    /// — the separate name only documents intent at call sites.</summary>
    public Task ApplyFetchedRelayModeAsync(string mode, CancellationToken cancellationToken = default) =>
        PersistAsync(mode, cancellationToken);

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
            JsonSerializer.Serialize(new PreferencesDocument(mode, null), JsonOptions),
            cancellationToken);
    }

    private string Load()
    {
        try
        {
            if (!File.Exists(_path)) return RelayModes.Automatic;
            var document = JsonSerializer.Deserialize<PreferencesDocument>(File.ReadAllText(_path), JsonOptions);
            if (document?.RelayMode is { } mode && RelayModes.IsValid(mode)) return mode;
            // Migrate the pre-existing boolean-only file shape (issue #26 predecessor).
            if (document?.ForceRelay is true) return RelayModes.ForceRelay;
            return RelayModes.Automatic;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/corrupt preferences file must never block startup; default to automatic.
            return RelayModes.Automatic;
        }
    }

    private sealed record PreferencesDocument(string? RelayMode, bool? ForceRelay);
}
