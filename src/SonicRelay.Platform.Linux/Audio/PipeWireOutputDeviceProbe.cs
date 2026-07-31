using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Lists PipeWire audio sinks for the source picker. `IAudioOutputDeviceProbe`
/// is defined as synchronous (matching the cheap local WASAPI enumeration this
/// contract was designed around); here it blocks on the underlying process
/// calls. Desktop composition (a later phase) is responsible for calling this
/// off the UI thread, matching how Settings already calls WASAPI enumeration.
/// </summary>
public sealed class PipeWireOutputDeviceProbe(ILinuxProcessRunner processRunner, PipeWireCommandPaths commandPaths) : IAudioOutputDeviceProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        try
        {
            return GetOutputDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<AudioOutputDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken)
    {
        var pwDump = await processRunner.RunAsync(commandPaths.PwDump, [], CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (pwDump.ExitCode != 0) return [];
        var sinks = PipeWireNodeParser.ParseSinks(pwDump.StandardOutput);
        if (sinks.Count == 0) return [];

        string? defaultNodeName = null;
        var defaultResult = await processRunner.RunAsync(
            commandPaths.Wpctl, ["inspect", "@DEFAULT_AUDIO_SINK@"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (defaultResult.ExitCode == 0) defaultNodeName = WpctlInspectParser.Parse(defaultResult.StandardOutput)?.NodeName;

        return sinks
            .Select(sink => new AudioOutputDevice(sink.NodeName, sink.DisplayName, sink.NodeName == defaultNodeName))
            .ToList();
    }
}
