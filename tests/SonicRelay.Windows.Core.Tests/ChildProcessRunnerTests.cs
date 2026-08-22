using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Windows.Core.Tests;

/// <summary>
/// These tests exercise <see cref="ChildProcessRunner"/> against real Unix
/// binaries (/bin/echo, /bin/sh, /bin/sleep, /bin/true) rather than fakes, to
/// validate actual `Process` behavior. Those paths exist on both Linux and
/// macOS — the two platforms whose capture adapters supervise a helper process
/// through this runner — so each test runs for real on either and no-ops on
/// Windows rather than failing on a missing binary.
/// </summary>
public sealed class LinuxProcessRunnerTests
{
    [Fact]
    public async Task RunAsyncCapturesStdoutAndExitCodeForARealProcess()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
        var result = await runner.RunAsync("/bin/echo", ["hello"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncReportsNonZeroExitCode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
        var result = await runner.RunAsync("/bin/sh", ["-c", "exit 3"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsyncKillsProcessAndThrowsOperationCanceledWhenCallerTokenIsCancelled()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
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
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
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
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
        var result = await runner.RunAsync("/bin/cat", [], TimeSpan.FromSeconds(5), CancellationToken.None, standardInput: "hello from stdin");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello from stdin", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncClosesStandardInputEvenWithoutInputSoAReaderDoesNotHang()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var runner = new ChildProcessRunner();
        // /bin/cat with no input reads until stdin is closed (EOF); if RunAsync never
        // closes it, this call would hang until the 5s timeout instead of returning fast.
        var result = await runner.RunAsync("/bin/cat", [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }
}
