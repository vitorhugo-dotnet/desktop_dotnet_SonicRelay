using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Tests.Shared.Fakes;
using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Platform.MacOs.Tests;

public sealed class ScreenRecordingPermissionProbeTests
{
    private const string HelperPath = "/Applications/SonicRelay.app/Contents/MacOS/sonicrelay-audio-tap";

    private static (ScreenRecordingPermissionProbe Probe, FakeChildProcessRunner Runner) Create(int? exitCode)
    {
        var runner = new FakeChildProcessRunner();
        if (exitCode is not null) runner.Script(HelperPath, new ChildProcessResult(exitCode.Value, string.Empty, string.Empty));
        return (new ScreenRecordingPermissionProbe(runner, HelperPath), runner);
    }

    [Fact]
    public async Task UsesTheNonPromptingCheckCommand()
    {
        var (probe, runner) = Create(AudioTapExitCode.Success);

        await probe.CheckAsync(CancellationToken.None);

        var (executable, arguments, _) = Assert.Single(runner.RunCalls);
        Assert.Equal(HelperPath, executable);
        Assert.Equal(["check-permission"], arguments);
    }

    [Theory]
    [InlineData(AudioTapExitCode.Success, ScreenRecordingPermission.Granted)]
    [InlineData(AudioTapExitCode.PermissionDenied, ScreenRecordingPermission.Denied)]
    [InlineData(AudioTapExitCode.UnsupportedOs, ScreenRecordingPermission.Unknown)]
    public async Task MapsTheHelpersExitCodeToAPermissionState(int exitCode, ScreenRecordingPermission expected)
    {
        var (probe, _) = Create(exitCode);

        Assert.Equal(expected, await probe.CheckAsync(CancellationToken.None));
    }

    /// <summary>
    /// A helper that cannot be launched says nothing about consent — reporting
    /// "denied" would send the user to System Settings to fix a broken install.
    /// </summary>
    [Fact]
    public async Task AnUnlaunchableHelperReportsUnknownRatherThanDenied()
    {
        var probe = new ScreenRecordingPermissionProbe(new ThrowingRunner(), HelperPath);

        Assert.Equal(ScreenRecordingPermission.Unknown, await probe.CheckAsync(CancellationToken.None));
    }

    private sealed class ThrowingRunner : IChildProcessRunner
    {
        public Task<ChildProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null) =>
            throw new FileNotFoundException(executable);

        public IChildProcess Start(string executable, IReadOnlyList<string> arguments) =>
            throw new FileNotFoundException(executable);
    }
}
