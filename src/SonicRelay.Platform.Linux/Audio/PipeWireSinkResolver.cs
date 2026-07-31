using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Resolves an explicit capture target for `pw-record`. `pw-record` must never
/// rely on automatic target selection for desktop-output capture: an automatic
/// target may resolve to a microphone instead of a sink/output monitor (spec:
/// docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md, ADR-LINUX-004).
/// </summary>
public sealed class PipeWireSinkResolver(ILinuxProcessRunner processRunner, PipeWireCommandPaths commandPaths)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<ResolvedSink> ResolveDefaultAsync(CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            commandPaths.Wpctl, ["inspect", "@DEFAULT_AUDIO_SINK@"], CommandTimeout, cancellationToken).ConfigureAwait(false);
        var resolved = result.ExitCode == 0 ? WpctlInspectParser.Parse(result.StandardOutput) : null;
        return resolved ?? throw new AudioCaptureException(AudioCaptureError.NoDevice, "No default PipeWire audio sink is available.");
    }

    /// <summary>
    /// Re-runs discovery, and if the saved <paramref name="nodeName"/> is still
    /// present, resolves its live target; otherwise falls back to the current
    /// default sink rather than failing capture outright.
    /// </summary>
    public async Task<ResolvedSink> ResolveByNodeNameAsync(string nodeName, CancellationToken cancellationToken)
    {
        var pwDump = await processRunner.RunAsync(commandPaths.PwDump, [], CommandTimeout, cancellationToken).ConfigureAwait(false);
        var sinks = pwDump.ExitCode == 0 ? PipeWireNodeParser.ParseSinks(pwDump.StandardOutput) : [];
        if (sinks.All(sink => sink.NodeName != nodeName))
        {
            return await ResolveDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await processRunner.RunAsync(
            commandPaths.Wpctl, ["inspect", nodeName], CommandTimeout, cancellationToken).ConfigureAwait(false);
        var resolved = result.ExitCode == 0 ? WpctlInspectParser.Parse(result.StandardOutput) : null;
        return resolved ?? await ResolveDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}
