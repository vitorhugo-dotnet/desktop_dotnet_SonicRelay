using SonicRelay.Windows.Audio;

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

public sealed record PipeWireCommandPaths(string PwDump, string PwRecord, string Wpctl, string? SecretTool);

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
        return new PipeWireCommandPaths(pwDump, pwRecord, wpctl, secretTool);
    }

    private static AudioCaptureException Missing(string tool) => new(
        AudioCaptureError.PlatformFailure,
        $"Required PipeWire tool '{tool}' was not found on PATH. Install the PipeWire/WirePlumber user tools package for your distribution.");
}
