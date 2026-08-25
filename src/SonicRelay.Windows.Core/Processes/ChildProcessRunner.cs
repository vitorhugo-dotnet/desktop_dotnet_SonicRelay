using System.Diagnostics;
using System.Text;

namespace SonicRelay.Windows.Core.Processes;

public sealed record ChildProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IChildProcess : IAsyncDisposable
{
    Stream StandardOutput { get; }

    /// <summary>
    /// The process's stdin, for helpers that are fed rather than read — the PipeWire playback
    /// process takes raw PCM this way. Closing it is how such a helper is asked to finish,
    /// which <see cref="StopAsync"/> already does.
    /// </summary>
    Stream StandardInput { get; }

    event Action<int>? Exited;
    Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
}

public interface IChildProcessRunner
{
    Task<ChildProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null);

    IChildProcess Start(string executable, IReadOnlyList<string> arguments);
}

/// <summary>
/// Launches a platform helper process directly via <see cref="ProcessStartInfo.ArgumentList"/>,
/// never through a shell. Shared by the Linux PipeWire adapter (which drives
/// `pw-record`/`pw-dump`/`wpctl`) and the macOS adapter (which drives the bundled
/// ScreenCaptureKit tap helper), so both get the same argument-escaping, timeout,
/// orphan-kill, and late-exit-subscriber semantics.
/// </summary>
public sealed class ChildProcessRunner : IChildProcessRunner
{
    private const int MaxCapturedChars = 2 * 1024 * 1024;

    public async Task<ChildProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var startInfo = BuildStartInfo(executable, arguments);
        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null && stdout.Length < MaxCapturedChars) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < MaxCapturedChars) stderr.Append(e.Data).Append('\n'); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Always close stdin, even when nothing is written: a command that reads
        // until EOF (e.g. `secret-tool store`, or `cat` in tests) would otherwise
        // block until the timeout instead of completing once its input is done.
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{executable} did not exit within {timeout}.");
        }

        return new ChildProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public IChildProcess Start(string executable, IReadOnlyList<string> arguments) =>
        new ChildProcess(new Process { StartInfo = BuildStartInfo(executable, arguments), EnableRaisingEvents = true });

    private static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }
}

internal sealed class ChildProcess : IChildProcess
{
    private const int MaxCapturedStderrChars = 8192;
    private readonly Process process;
    private readonly StringBuilder stderr = new();
    private readonly object exitGate = new();
    private Action<int>? exitedHandlers;
    private int? exitCode;
    private bool disposed;

    public ChildProcess(Process process)
    {
        this.process = process;
        this.process.Exited += OnProcessExited;
        this.process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < MaxCapturedStderrChars) stderr.Append(e.Data).Append('\n'); };
        this.process.Start();
        this.process.BeginErrorReadLine();
    }

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Stream StandardInput => process.StandardInput.BaseStream;

    public event Action<int>? Exited
    {
        add
        {
            bool alreadyExited;
            int code;
            lock (exitGate)
            {
                exitedHandlers += value;
                alreadyExited = exitCode.HasValue;
                code = exitCode.GetValueOrDefault();
            }
            // Replay to a late subscriber so a fast-exiting/failed-to-start process
            // is never silently missed (issue #32 Linux adapter review finding).
            if (alreadyExited) value?.Invoke(code);
        }
        remove
        {
            lock (exitGate) { exitedHandlers -= value; }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = SafeExitCode();
        Action<int>? handlers;
        lock (exitGate)
        {
            exitCode = code;
            handlers = exitedHandlers;
        }
        handlers?.Invoke(code);
    }

    public string RecentStandardError => stderr.ToString();

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        if (process.HasExited) return;
        try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        try
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(gracePeriod);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ChildProcessRunner.KillTree(process);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (!process.HasExited) ChildProcessRunner.KillTree(process);
        process.Dispose();
        await Task.CompletedTask;
    }

    private int SafeExitCode()
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch (InvalidOperationException) { return -1; }
    }
}
