using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests.Fakes;

internal sealed class FakeLinuxProcess : ILinuxProcess
{
    private readonly MemoryStream stdout = new();
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }

    public Stream StandardOutput => stdout;
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

internal sealed class FakeLinuxProcessRunner : ILinuxProcessRunner
{
    private readonly Dictionary<string, LinuxProcessResult> scriptedResults = new();
    public List<(string Executable, IReadOnlyList<string> Arguments, string? StandardInput)> RunCalls { get; } = [];
    public List<(string Executable, IReadOnlyList<string> Arguments)> StartCalls { get; } = [];
    public FakeLinuxProcess? LastStartedProcess { get; private set; }

    public void Script(string executable, LinuxProcessResult result) => scriptedResults[executable] = result;

    public Task<LinuxProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        RunCalls.Add((executable, arguments, standardInput));
        return Task.FromResult(scriptedResults.TryGetValue(executable, out var result)
            ? result
            : new LinuxProcessResult(1, string.Empty, "not scripted"));
    }

    public ILinuxProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        StartCalls.Add((executable, arguments));
        LastStartedProcess = new FakeLinuxProcess();
        return LastStartedProcess;
    }
}
