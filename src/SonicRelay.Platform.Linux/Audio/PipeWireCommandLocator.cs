using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.Linux.Audio;

public interface IExecutableLocator
{
    string? Locate(string executableName);
}

/// <summary>Scans PATH directories for an executable file, without invoking a shell.</summary>
public sealed class PathExecutableLocator : IExecutableLocator
{
    public string? Locate(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public sealed record PipeWireCommandPaths(
    string PwDump,
    string PwRecord,
    string Wpctl,
    string? SecretTool,
    /// Optional: only two-way sessions need to play audio back, so a machine without it can
    /// still publish and the duplex controls simply report playback unavailable.
    string? PwPlay = null);

/// <summary>
/// Resolves the PipeWire/WirePlumber CLI tools the Linux adapter shells out to.
/// `secret-tool` is optional here (only required for token storage in a later
/// phase); `pw-dump`, `pw-record`, and `wpctl` are mandatory for audio capture.
/// </summary>
public sealed class PipeWireCommandLocator
{
    private readonly IExecutableLocator locator;

    public PipeWireCommandLocator() : this(new PathExecutableLocator()) { }

    public PipeWireCommandLocator(IExecutableLocator locator) => this.locator = locator;

    public PipeWireCommandPaths Locate()
    {
        var pwDump = locator.Locate("pw-dump") ?? throw Missing("pw-dump");
        var pwRecord = locator.Locate("pw-record") ?? throw Missing("pw-record");
        var wpctl = locator.Locate("wpctl") ?? throw Missing("wpctl");
        var secretTool = locator.Locate("secret-tool");
        // `pw-play` ships in the same package as `pw-record`, but it is resolved optionally so
        // an install that somehow lacks it still publishes instead of failing to launch.
        var pwPlay = locator.Locate("pw-play") ?? locator.Locate("pw-cat");
        return new PipeWireCommandPaths(pwDump, pwRecord, wpctl, secretTool, pwPlay);
    }

    private static AudioCaptureException Missing(string tool) => new(
        AudioCaptureError.PlatformFailure,
        $"Required PipeWire tool '{tool}' was not found on PATH. Install the PipeWire/WirePlumber user tools package for your distribution.");
}
