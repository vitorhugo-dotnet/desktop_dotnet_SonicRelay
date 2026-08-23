using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Audio;

/// <summary>Filesystem probe seam so locator resolution is testable without a real bundle.</summary>
public interface IFileExistenceProbe
{
    bool Exists(string path);
}

internal sealed class FileExistenceProbe : IFileExistenceProbe
{
    public bool Exists(string path) => File.Exists(path);
}

/// <summary>
/// Resolves the bundled <c>sonicrelay-audio-tap</c> helper.
///
/// Unlike the Linux adapter — which locates PipeWire tools the distribution
/// installs on PATH — this helper ships inside SonicRelay's own app bundle and
/// is never expected on PATH. That is a hard requirement of macOS privacy
/// enforcement, not a packaging preference: TCC grants Screen Recording consent
/// to a code-signed bundle identity, so the helper only inherits SonicRelay's
/// grant while it lives inside (and is signed as part of) SonicRelay.app.
/// Resolving a stray copy elsewhere on the system would silently produce a
/// process the user never consented to.
/// </summary>
public sealed class AudioTapLocator
{
    /// <summary>Name of the helper executable inside the bundle.</summary>
    public const string ExecutableName = "sonicrelay-audio-tap";

    /// <summary>
    /// Overrides the resolved path. Exists for development against a helper
    /// built outside a bundle (packaging/macos/build-audio-tap.sh writes one),
    /// where there is no SonicRelay.app to look inside.
    /// </summary>
    public const string OverrideVariable = "SONICRELAY_AUDIO_TAP";

    private readonly IFileExistenceProbe probe;
    private readonly string baseDirectory;
    private readonly Func<string, string?> environment;

    public AudioTapLocator() : this(new FileExistenceProbe(), AppContext.BaseDirectory, Environment.GetEnvironmentVariable) { }

    public AudioTapLocator(IFileExistenceProbe probe, string baseDirectory, Func<string, string?> environment)
    {
        this.probe = probe;
        this.baseDirectory = baseDirectory;
        this.environment = environment;
    }

    /// <summary>
    /// Returns the helper's absolute path, or throws an
    /// <see cref="AudioCaptureException"/> naming what is missing. Throwing
    /// (rather than returning null) matches <c>PipeWireCommandLocator</c>: a
    /// missing capture dependency is surfaced as an actionable capture error at
    /// composition time, and the shell keeps running without audio rather than
    /// failing to start.
    /// </summary>
    public string Locate()
    {
        var overridePath = environment(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (probe.Exists(overridePath)) return overridePath;
            throw new AudioCaptureException(
                AudioCaptureError.PlatformFailure,
                $"{OverrideVariable} points at '{overridePath}', which does not exist.");
        }

        foreach (var candidate in CandidatePaths())
        {
            if (probe.Exists(candidate)) return candidate;
        }

        throw new AudioCaptureException(
            AudioCaptureError.PlatformFailure,
            $"The bundled system audio helper '{ExecutableName}' was not found next to the application. Reinstall SonicRelay from the official macOS package, or set {OverrideVariable} to a locally built helper.");
    }

    /// <summary>
    /// The two layouts a published build can have: the helper sits beside the
    /// app binary in <c>SonicRelay.app/Contents/MacOS</c> (what
    /// packaging/macos/build-app-bundle.sh produces), or beside a plain
    /// <c>dotnet publish</c> output during development.
    /// </summary>
    private IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(baseDirectory, ExecutableName);
        yield return Path.Combine(baseDirectory, "..", "Resources", ExecutableName);
    }
}
