using SonicRelay.Windows.Core.Processes;

namespace SonicRelay.Tests.Shared.Fakes;

internal sealed class FakeChildProcess : IChildProcess
{
    private readonly MemoryStream stdout = new();

    /// <summary>Everything the caller fed the process, for helpers driven through stdin.</summary>
    public MemoryStream WrittenInput { get; } = new();

    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }

    public Stream StandardOutput => stdout;
    public Stream StandardInput => WrittenInput;
    public event Action<int>? Exited;

    public void Write(byte[] data)
    {
        var position = stdout.Position;
        stdout.Seek(0, SeekOrigin.End);
        stdout.Write(data, 0, data.Length);
        stdout.Position = position;
    }

    public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);

    public Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeChildProcessRunner : IChildProcessRunner
{
    private readonly Dictionary<string, ChildProcessResult> scriptedResults = new();
    public List<(string Executable, IReadOnlyList<string> Arguments, string? StandardInput)> RunCalls { get; } = [];
    public List<(string Executable, IReadOnlyList<string> Arguments)> StartCalls { get; } = [];
    public FakeChildProcess? LastStartedProcess { get; private set; }

    public void Script(string executable, ChildProcessResult result) => scriptedResults[executable] = result;

    public Task<ChildProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        RunCalls.Add((executable, arguments, standardInput));
        return Task.FromResult(scriptedResults.TryGetValue(executable, out var result)
            ? result
            : new ChildProcessResult(1, string.Empty, "not scripted"));
    }

    public IChildProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        StartCalls.Add((executable, arguments));
        LastStartedProcess = new FakeChildProcess();
        return LastStartedProcess;
    }
}
