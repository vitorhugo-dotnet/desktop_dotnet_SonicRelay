using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

/// <summary>
/// These tests exercise <see cref="LinuxProcessRunner"/> against real Unix
/// binaries (/bin/echo, /bin/sh, /bin/sleep, /bin/true) rather than fakes, to
/// validate actual `Process` behavior. This repo's CI currently only runs a
/// windows-latest job (a Linux matrix is deferred to a later phase of issue
/// #32 — see docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md),
/// so each test no-ops on non-Linux hosts instead of failing on a missing
/// binary; they still run for real on Linux (this sandbox, and eventually CI).
/// </summary>
public sealed class LinuxProcessRunnerTests
{
    [Fact]
    public async Task RunAsyncCapturesStdoutAndExitCodeForARealProcess()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        var result = await runner.RunAsync("/bin/echo", ["hello"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncReportsNonZeroExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        var result = await runner.RunAsync("/bin/sh", ["-c", "exit 3"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsyncKillsProcessAndThrowsOperationCanceledWhenCallerTokenIsCancelled()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        using var cts = new CancellationTokenSource();

        var runTask = runner.RunAsync("/bin/sleep", ["30"], TimeSpan.FromSeconds(30), cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        cts.Cancel();

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(runTask, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ExitedNotifiesLateSubscriberForAlreadyExitedProcess()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        await using var process = runner.Start("/bin/true", []);

        // Give the process time to actually exit before we subscribe, so we
        // exercise the "subscriber attaches after Exited already fired" race.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var exitCodeReceived = new TaskCompletionSource<int>();
        process.Exited += code => exitCodeReceived.TrySetResult(code);

        var result = await exitCodeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task RunAsyncWritesStandardInputAndClosesItBeforeWaitingForExit()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        var result = await runner.RunAsync("/bin/cat", [], TimeSpan.FromSeconds(5), CancellationToken.None, standardInput: "hello from stdin");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello from stdin", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncClosesStandardInputEvenWithoutInputSoAReaderDoesNotHang()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new LinuxProcessRunner();
        // /bin/cat with no input reads until stdin is closed (EOF); if RunAsync never
        // closes it, this call would hang until the 5s timeout instead of returning fast.
        var result = await runner.RunAsync("/bin/cat", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }
}
