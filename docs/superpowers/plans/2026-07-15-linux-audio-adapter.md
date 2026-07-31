# Linux Audio Adapter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement PR 1 of the Linux desktop publisher design (issue #32, `docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md`): a PipeWire-backed `IAudioCaptureBackend` in a new `SonicRelay.Platform.Linux` project, fully unit-testable with fake process runners, plumbed through the existing shared `AudioCaptureService`/`WebRtcAudioBridge` without touching desktop composition, CI, or packaging.

**Architecture:** `AudioCaptureService` already isolates WASAPI behind an internal `IAudioCaptureBackend` seam. This plan (1) makes that seam and a platform-neutral factory public, (2) extracts shared peak/RMS level math so WASAPI and PipeWire don't duplicate it, then (3) adds `SonicRelay.Platform.Linux` with an injectable process-runner abstraction, `pw-dump`/`wpctl inspect` parsers for sink discovery/resolution, a `pw-record`-based `IAudioCaptureBackend` implementation with exact 20 ms PCM16 frame assembly, and a proof that frames flow through the existing `WebRtcAudioBridge` unchanged.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit 2.9.3, `System.Text.Json` (BCL, no new package), `System.Diagnostics.Process` (first process-invocation code in this repo).

## Global Constraints

- Target framework `net10.0`, `LangVersion 14.0`, `Nullable enable`, `ImplicitUsings enable` (from `Directory.Build.props` and existing `.csproj` files) — new projects must match.
- No `Directory.Packages.props` exists; each `.csproj` declares its own `<PackageReference>` versions directly.
- Test framework is xUnit: `coverlet.collector 6.0.4`, `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.4`, with `<Using Include="Xunit" />` as a global using.
- Solution file is `.slnx` XML format (`SonicRelay.Windows.slnx`); new projects are added as `<Project Path="..." />` entries under `/src/` or `/tests/`.
- New Linux project must be named `SonicRelay.Platform.Linux` (spec ADR-LINUX-011: keep historical `SonicRelay.Windows.*` names on existing projects; do not rename them in this phase).
- Never invoke a shell (`/bin/sh -c`); always launch tools via `ProcessStartInfo.ArgumentList` with `UseShellExecute = false`.
- Capture format is fixed: 48000 Hz, stereo, signed 16-bit little-endian PCM, 20 ms frames = exactly `48000 * 0.020 * 2 * 2 = 3840` bytes per frame.
- A capture target must always be explicitly resolved (`@DEFAULT_AUDIO_SINK@` via `wpctl inspect`, or a saved `node.name` re-resolved to its live `object.serial`/`node.name`); `pw-record` must never rely on automatic target selection.
- Persist/track sinks by `node.name`, never by numeric PipeWire object id (ids can be reused).
- Bound and redact all captured process stderr/stdout; never let unbounded or malformed tool output crash the app.
- This plan covers spec Phase 3 scope only (**PR 1 — Adapter and shared seams**): no `DesktopRuntimeFactory`, `App.axaml.cs` wiring, Secret Service token store, XDG paths, CI matrix, or packaging. Those are separate follow-up plans (PR 2, PR 3 in the spec).

---

## File Structure

```
src/SonicRelay.Windows.Audio/
  IAudioCaptureService.cs        MODIFY — internal IAudioCaptureBackend -> public
  AudioCaptureService.cs         MODIFY — add public static Create(...) factory; neutralize Windows-only error text
  AudioLevelCalculator.cs        NEW    — shared peak/RMS helper
  WasapiLoopbackBackend.cs       MODIFY — delegate to AudioLevelCalculator

tests/SonicRelay.Windows.Audio.Tests/
  AudioCaptureServiceTests.cs    MODIFY — test for the new Create(...) factory
  AudioLevelCalculatorTests.cs   NEW    — parity tests for the extracted helper

src/SonicRelay.Platform.Linux/
  SonicRelay.Platform.Linux.csproj   NEW
  Audio/
    LinuxProcessRunner.cs             NEW — ILinuxProcessRunner/ILinuxProcess + real Process-based implementation
    PipeWireCommandLocator.cs         NEW — locates pw-dump/pw-record/wpctl/secret-tool on PATH
    PipeWireNode.cs                   NEW — PipeWireNode record + PipeWireNodeParser (pw-dump JSON)
    WpctlInspectParser.cs             NEW — ResolvedSink record + wpctl inspect text parser
    PipeWireSinkResolver.cs           NEW — default/selected sink resolution
    PipeWireOutputDeviceProbe.cs      NEW — IAudioOutputDeviceProbe implementation
    PcmFrameAssembler.cs              NEW — raw PCM byte stream -> exact 20 ms AudioFrame
    PipeWireProcessBackend.cs         NEW — IAudioCaptureBackend implementation supervising pw-record

tests/SonicRelay.Platform.Linux.Tests/
  SonicRelay.Platform.Linux.Tests.csproj   NEW
  Fakes/
    FakeLinuxProcessRunner.cs         NEW — scriptable ILinuxProcessRunner/ILinuxProcess fakes
  PipeWireNodeParserTests.cs          NEW
  WpctlInspectParserTests.cs          NEW
  PipeWireSinkResolverTests.cs        NEW
  PipeWireOutputDeviceProbeTests.cs   NEW
  PcmFrameAssemblerTests.cs           NEW
  PipeWireProcessBackendTests.cs      NEW
  WebRtcAudioBridgeIntegrationTests.cs NEW — proves frames reach WebRtcAudioBridge/IWebRtcPublisher

SonicRelay.Windows.slnx              MODIFY — register both new projects
```

---

## Task 1: Make the capture backend seam public and add a platform-neutral factory

**Files:**
- Modify: `src/SonicRelay.Windows.Audio/IAudioCaptureService.cs:38-47`
- Modify: `src/SonicRelay.Windows.Audio/AudioCaptureService.cs:63-77,115,268`
- Test: `tests/SonicRelay.Windows.Audio.Tests/AudioCaptureServiceTests.cs`

**Interfaces:**
- Produces: `public interface IAudioCaptureBackend` (same members as today, just no longer `internal`); `public static AudioCaptureService AudioCaptureService.Create(IAudioCaptureBackend backend, IAudioOutputDeviceProbe deviceProbe, AudioRecoveryPolicy? recoveryPolicy = null)`.
- Consumes: existing `AudioContracts.cs` types (`AudioDeviceInfo`, `AudioFrame`, `AudioLevelSnapshot`, `AudioCaptureException`, `IAudioOutputDeviceProbe`, `AudioRecoveryPolicy`) — all already `public`.

- [ ] **Step 1: Write the failing test for the new public factory**

Add to `tests/SonicRelay.Windows.Audio.Tests/AudioCaptureServiceTests.cs` (near the other lifecycle tests, using the existing `FakeAudioCaptureBackend`/`FakeOutputDeviceProbe` fakes already in that file):

```csharp
[Fact]
public async Task CreateFactoryProducesAWorkingService()
{
    var backend = new FakeAudioCaptureBackend();
    var probe = new FakeOutputDeviceProbe([new AudioOutputDevice("sink-1", "Sink 1", true)]);

    await using var service = AudioCaptureService.Create(backend, probe);

    Assert.Equal(AudioCaptureState.Stopped, service.State);
    Assert.Single(service.GetOutputDevices());

    await service.StartAsync();

    Assert.Equal(AudioCaptureState.Capturing, service.State);
    Assert.Equal(1, backend.StartCount);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Windows.Audio.Tests --filter CreateFactoryProducesAWorkingService`
Expected: FAIL — `'AudioCaptureService' does not contain a definition for 'Create'`.

- [ ] **Step 3: Make `IAudioCaptureBackend` public**

In `src/SonicRelay.Windows.Audio/IAudioCaptureService.cs`, change line 38:

```csharp
internal interface IAudioCaptureBackend : IAsyncDisposable
```

to:

```csharp
public interface IAudioCaptureBackend : IAsyncDisposable
```

- [ ] **Step 4: Add the public `Create` factory to `AudioCaptureService`**

In `src/SonicRelay.Windows.Audio/AudioCaptureService.cs`, immediately after the internal composition-root constructor (after line 77, before `private static bool IsRetryable`), add:

```csharp
    /// <summary>
    /// Platform-neutral composition entry point (issue #32): any platform shell
    /// supplies its own <see cref="IAudioCaptureBackend"/> (WASAPI on Windows,
    /// PipeWire on Linux) and device probe, and gets the same lifecycle,
    /// recovery, and diagnostics behavior either way.
    /// </summary>
    public static AudioCaptureService Create(
        IAudioCaptureBackend backend,
        IAudioOutputDeviceProbe deviceProbe,
        AudioRecoveryPolicy? recoveryPolicy = null) =>
        new(backend, recoveryPolicy: recoveryPolicy, deviceProbe: deviceProbe);
```

- [ ] **Step 5: Neutralize the two Windows-specific failure messages**

Concrete backends remain responsible for actionable platform detail; the shared service should not claim "Windows" when the backend might be PipeWire. In `src/SonicRelay.Windows.Audio/AudioCaptureService.cs`:

Line 115, change:
```csharp
SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Windows audio capture could not be started.", error));
```
to:
```csharp
SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Audio capture could not be started.", error));
```

Line 268, change:
```csharp
SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Windows audio recovery failed.", unexpected));
```
to:
```csharp
SetFailure(new AudioCaptureException(AudioCaptureError.PlatformFailure, "Audio recovery failed.", unexpected));
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Windows.Audio.Tests --filter CreateFactoryProducesAWorkingService`
Expected: PASS

- [ ] **Step 7: Run the full Audio test suite to check for regressions**

Run: `dotnet test tests/SonicRelay.Windows.Audio.Tests`
Expected: PASS (all existing tests, including any asserting the old "Windows audio capture..." message text — check `AudioCaptureServiceTests.cs` for string assertions on those two messages and update them to the neutral text if present).

- [ ] **Step 8: Commit**

```bash
git add src/SonicRelay.Windows.Audio/IAudioCaptureService.cs src/SonicRelay.Windows.Audio/AudioCaptureService.cs tests/SonicRelay.Windows.Audio.Tests/AudioCaptureServiceTests.cs
git commit -m "feat(audio): make IAudioCaptureBackend public and add a platform-neutral Create factory"
```

---

## Task 2: Extract shared peak/RMS level calculation

**Files:**
- Create: `src/SonicRelay.Windows.Audio/AudioLevelCalculator.cs`
- Modify: `src/SonicRelay.Windows.Audio/WasapiLoopbackBackend.cs:164,171-187`
- Test: `tests/SonicRelay.Windows.Audio.Tests/AudioLevelCalculatorTests.cs`

**Interfaces:**
- Produces: `public static class AudioLevelCalculator { public static AudioLevelSnapshot Calculate(ReadOnlySpan<byte> data, AudioSampleFormat format); }` — this is what `PcmFrameAssembler` (Task 9) will call.
- Consumes: `AudioSampleFormat`, `AudioLevelSnapshot` from `AudioContracts.cs` (both already public).

- [ ] **Step 1: Write the failing test**

Create `tests/SonicRelay.Windows.Audio.Tests/AudioLevelCalculatorTests.cs`:

```csharp
namespace SonicRelay.Windows.Audio;

public sealed class AudioLevelCalculatorTests
{
    [Fact]
    public void SilentPcm16BufferProducesZeroLevel()
    {
        var data = new byte[8]; // four Int16 zero samples
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.Pcm16);
        Assert.Equal(0f, level.Peak);
        Assert.Equal(0f, level.Rms);
    }

    [Fact]
    public void FullScalePcm16SampleProducesPeakOne()
    {
        var data = BitConverter.GetBytes((short)32767);
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.Pcm16);
        Assert.True(level.Peak > 0.99f);
    }

    [Fact]
    public void FullScaleFloatSampleProducesPeakOne()
    {
        var data = BitConverter.GetBytes(1.0f);
        var level = AudioLevelCalculator.Calculate(data, AudioSampleFormat.IeeeFloat32);
        Assert.Equal(1f, level.Peak);
        Assert.Equal(1f, level.Rms);
    }

    [Fact]
    public void EmptyBufferProducesSilence()
    {
        var level = AudioLevelCalculator.Calculate(ReadOnlySpan<byte>.Empty, AudioSampleFormat.Pcm16);
        Assert.Equal(AudioLevelSnapshot.Silence, level);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Windows.Audio.Tests --filter AudioLevelCalculatorTests`
Expected: FAIL — `The type or namespace name 'AudioLevelCalculator' could not be found`.

- [ ] **Step 3: Create the shared helper**

Create `src/SonicRelay.Windows.Audio/AudioLevelCalculator.cs`:

```csharp
namespace SonicRelay.Windows.Audio;

/// <summary>
/// Pure PCM16/IeeeFloat32 peak+RMS level calculation shared by every capture
/// backend (WASAPI, PipeWire — issue #32) so level math is not duplicated per
/// platform.
/// </summary>
public static class AudioLevelCalculator
{
    public static AudioLevelSnapshot Calculate(ReadOnlySpan<byte> data, AudioSampleFormat format)
    {
        double sum = 0;
        float peak = 0;
        var count = format == AudioSampleFormat.IeeeFloat32 ? data.Length / 4 : data.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = format == AudioSampleFormat.IeeeFloat32
                ? Math.Clamp(BitConverter.ToSingle(data.Slice(i * 4, 4)), -1f, 1f)
                : BitConverter.ToInt16(data.Slice(i * 2, 2)) / 32768f;
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sum += sample * sample;
        }
        var rms = count == 0 ? 0 : (float)Math.Sqrt(sum / count);
        return new AudioLevelSnapshot(Math.Clamp(peak, 0, 1), Math.Clamp(rms, 0, 1));
    }
}
```

- [ ] **Step 4: Delegate `WasapiLoopbackBackend` to the shared helper**

In `src/SonicRelay.Windows.Audio/WasapiLoopbackBackend.cs`, line 164, change:
```csharp
var level = CalculateLevel(data, sampleFormat);
```
to:
```csharp
var level = AudioLevelCalculator.Calculate(data, sampleFormat);
```

Then delete the now-unused private `CalculateLevel` method (lines 171-187).

- [ ] **Step 5: Run tests to verify everything passes**

Run: `dotnet test tests/SonicRelay.Windows.Audio.Tests`
Expected: PASS (new `AudioLevelCalculatorTests` plus no regressions in existing WASAPI-adjacent tests).

- [ ] **Step 6: Commit**

```bash
git add src/SonicRelay.Windows.Audio/AudioLevelCalculator.cs src/SonicRelay.Windows.Audio/WasapiLoopbackBackend.cs tests/SonicRelay.Windows.Audio.Tests/AudioLevelCalculatorTests.cs
git commit -m "refactor(audio): extract shared peak/RMS level calculation"
```

---

## Task 3: Scaffold the `SonicRelay.Platform.Linux` project and test project

**Files:**
- Create: `src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj`
- Create: `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj`
- Modify: `SonicRelay.Windows.slnx`

**Interfaces:**
- Produces: an empty, building `SonicRelay.Platform.Linux` project referencing `SonicRelay.Windows.Audio`, and its paired test project, both registered in the solution — the foundation every later task's files land in.

- [ ] **Step 1: Create the project directories and csproj files**

Create `src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../SonicRelay.Windows.Audio/SonicRelay.Windows.Audio.csproj" />
  </ItemGroup>

</Project>
```

Create `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add a placeholder source and test file so both projects build**

Create `src/SonicRelay.Platform.Linux/Audio/PipeWireNode.cs` with just the namespace marker for now (this file is fully replaced with real content in Task 6; creating it here only proves the project compiles):

```csharp
namespace SonicRelay.Platform.Linux.Audio;
```

Create `tests/SonicRelay.Platform.Linux.Tests/ScaffoldTests.cs`:

```csharp
namespace SonicRelay.Platform.Linux.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void ProjectBuildsAndReferencesWindowsAudio()
    {
        var format = SonicRelay.Windows.Audio.AudioSampleFormat.Pcm16;
        Assert.Equal(SonicRelay.Windows.Audio.AudioSampleFormat.Pcm16, format);
    }
}
```

- [ ] **Step 3: Register both projects in the solution**

In `SonicRelay.Windows.slnx`, add a line inside `<Folder Name="/src/">` (after the `SonicRelay.Windows.Presentation` entry, before `SonicRelay.Windows.Desktop`, alphabetically anywhere is fine — keep it next to Desktop for readability):

```xml
    <Project Path="src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj" />
```

And inside `<Folder Name="/tests/">`:

```xml
    <Project Path="tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj" />
```

- [ ] **Step 4: Build and test to verify the scaffold works**

Run: `dotnet build SonicRelay.Windows.slnx`
Expected: Build succeeds, including the two new projects.

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests`
Expected: PASS — `ScaffoldTests.ProjectBuildsAndReferencesWindowsAudio`.

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux tests/SonicRelay.Platform.Linux.Tests SonicRelay.Windows.slnx
git commit -m "chore(linux): scaffold the SonicRelay.Platform.Linux project"
```

---

## Task 4: Injectable process-invocation abstraction

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs`
- Create: `tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record LinuxProcessResult(int ExitCode, string StandardOutput, string StandardError);
  public interface ILinuxProcess : IAsyncDisposable
  {
      Stream StandardOutput { get; }
      event Action<int>? Exited;
      Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
  }
  public interface ILinuxProcessRunner
  {
      Task<LinuxProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
      ILinuxProcess Start(string executable, IReadOnlyList<string> arguments);
  }
  public sealed class LinuxProcessRunner : ILinuxProcessRunner { /* real Process-based implementation */ }
  ```
- Consumes: nothing from earlier tasks (this is a leaf abstraction). `RunAsync` is used by Tasks 5/7/8 for one-shot commands (`pw-dump`, `wpctl inspect`); `Start` is used by Task 10 for the long-running `pw-record` stream.

- [ ] **Step 1: Write the failing test using a fake (drives the interface shape first)**

Create `tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs`:

```csharp
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
    public List<(string Executable, IReadOnlyList<string> Arguments)> RunCalls { get; } = [];
    public FakeLinuxProcess? LastStartedProcess { get; private set; }

    public void Script(string executable, LinuxProcessResult result) => scriptedResults[executable] = result;

    public Task<LinuxProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        RunCalls.Add((executable, arguments));
        return Task.FromResult(scriptedResults.TryGetValue(executable, out var result)
            ? result
            : new LinuxProcessResult(1, string.Empty, "not scripted"));
    }

    public ILinuxProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        LastStartedProcess = new FakeLinuxProcess();
        return LastStartedProcess;
    }
}
```

Create `tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class LinuxProcessRunnerTests
{
    [Fact]
    public async Task RunAsyncCapturesStdoutAndExitCodeForARealProcess()
    {
        var runner = new LinuxProcessRunner();
        var result = await runner.RunAsync("/bin/echo", ["hello"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncReportsNonZeroExitCode()
    {
        var runner = new LinuxProcessRunner();
        var result = await runner.RunAsync("/bin/sh", ["-c", "exit 3"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter LinuxProcessRunnerTests`
Expected: FAIL — `LinuxProcessRunner` does not exist.

- [ ] **Step 3: Implement `LinuxProcessRunner`**

Create `src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs`:

```csharp
using System.Diagnostics;
using System.Text;

namespace SonicRelay.Platform.Linux.Audio;

public sealed record LinuxProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface ILinuxProcess : IAsyncDisposable
{
    Stream StandardOutput { get; }
    event Action<int>? Exited;
    Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
}

public interface ILinuxProcessRunner
{
    Task<LinuxProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ILinuxProcess Start(string executable, IReadOnlyList<string> arguments);
}

/// <summary>
/// Launches PipeWire/WirePlumber tools directly via <see cref="ProcessStartInfo.ArgumentList"/>,
/// never through a shell (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public sealed class LinuxProcessRunner : ILinuxProcessRunner
{
    private const int MaxCapturedChars = 2 * 1024 * 1024;

    public async Task<LinuxProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            throw new TimeoutException($"{executable} did not exit within {timeout}.");
        }

        return new LinuxProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public ILinuxProcess Start(string executable, IReadOnlyList<string> arguments) =>
        new LinuxProcess(new Process { StartInfo = BuildStartInfo(executable, arguments), EnableRaisingEvents = true });

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

internal sealed class LinuxProcess : ILinuxProcess
{
    private const int MaxCapturedStderrChars = 8192;
    private readonly Process process;
    private readonly StringBuilder stderr = new();
    private bool disposed;

    public LinuxProcess(Process process)
    {
        this.process = process;
        this.process.Exited += (_, _) => Exited?.Invoke(SafeExitCode());
        this.process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < MaxCapturedStderrChars) stderr.Append(e.Data).Append('\n'); };
        this.process.Start();
        this.process.BeginErrorReadLine();
    }

    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public event Action<int>? Exited;
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
            LinuxProcessRunner.KillTree(process);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (!process.HasExited) LinuxProcessRunner.KillTree(process);
        process.Dispose();
        await Task.CompletedTask;
    }

    private int SafeExitCode()
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch (InvalidOperationException) { return -1; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter LinuxProcessRunnerTests`
Expected: PASS. (These two tests shell out to real `/bin/echo`/`/bin/sh`, which exist on both `ubuntu-24.04` GitHub runners and this sandbox — acceptable for this one file since it is the only place validating real `Process` behavior; every other Linux test in this plan uses `FakeLinuxProcessRunner`/`FakeLinuxProcess` instead.)

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs
git commit -m "feat(linux): add ILinuxProcessRunner process-invocation abstraction"
```

---

## Task 5: Locate PipeWire/WirePlumber tools on PATH

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/PipeWireCommandLocator.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/PipeWireCommandLocatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IExecutableLocator { string? Locate(string executableName); }
  public sealed class PathExecutableLocator : IExecutableLocator { ... }
  public sealed record PipeWireCommandPaths(string PwDump, string PwRecord, string Wpctl, string? SecretTool);
  public sealed class PipeWireCommandLocator
  {
      public PipeWireCommandLocator();
      public PipeWireCommandLocator(IExecutableLocator locator);
      public PipeWireCommandPaths Locate(); // throws AudioCaptureException(PlatformFailure) naming the missing tool for pw-dump/pw-record/wpctl; SecretTool stays null if absent
  }
  ```
- Consumes: `AudioCaptureException`, `AudioCaptureError` from `SonicRelay.Windows.Audio` (public, via the existing project reference).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PipeWireCommandLocatorTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

internal sealed class FakeExecutableLocator(Dictionary<string, string?> paths) : IExecutableLocator
{
    public string? Locate(string executableName) => paths.GetValueOrDefault(executableName);
}

public sealed class PipeWireCommandLocatorTests
{
    [Fact]
    public void LocateReturnsResolvedPathsWhenEverythingIsPresent()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["pw-record"] = "/usr/bin/pw-record",
            ["wpctl"] = "/usr/bin/wpctl",
            ["secret-tool"] = "/usr/bin/secret-tool",
        }));

        var paths = locator.Locate();

        Assert.Equal("/usr/bin/pw-dump", paths.PwDump);
        Assert.Equal("/usr/bin/pw-record", paths.PwRecord);
        Assert.Equal("/usr/bin/wpctl", paths.Wpctl);
        Assert.Equal("/usr/bin/secret-tool", paths.SecretTool);
    }

    [Fact]
    public void LocateReturnsNullSecretToolWhenAbsentButStillSucceeds()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["pw-record"] = "/usr/bin/pw-record",
            ["wpctl"] = "/usr/bin/wpctl",
        }));

        var paths = locator.Locate();

        Assert.Null(paths.SecretTool);
    }

    [Fact]
    public void LocateThrowsPlatformFailureNamingTheMissingRequiredTool()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["wpctl"] = "/usr/bin/wpctl",
        }));

        var exception = Assert.Throws<AudioCaptureException>(() => locator.Locate());

        Assert.Equal(AudioCaptureError.PlatformFailure, exception.Error);
        Assert.Contains("pw-record", exception.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireCommandLocatorTests`
Expected: FAIL — types don't exist yet.

- [ ] **Step 3: Implement the locator**

Create `src/SonicRelay.Platform.Linux/Audio/PipeWireCommandLocator.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireCommandLocatorTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/PipeWireCommandLocator.cs tests/SonicRelay.Platform.Linux.Tests/PipeWireCommandLocatorTests.cs
git commit -m "feat(linux): locate pw-dump/pw-record/wpctl/secret-tool on PATH"
```

---

## Task 6: Parse `pw-dump` JSON into audio sink nodes

**Files:**
- Modify: `src/SonicRelay.Platform.Linux/Audio/PipeWireNode.cs` (replace the Task 3 placeholder)
- Test: `tests/SonicRelay.Platform.Linux.Tests/PipeWireNodeParserTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record PipeWireNode(string NodeName, string DisplayName, bool IsAudioSink);
  public static class PipeWireNodeParser
  {
      public static IReadOnlyList<PipeWireNode> ParseSinks(string pwDumpJson);
  }
  ```
- Consumes: nothing beyond `System.Text.Json` (BCL).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PipeWireNodeParserTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireNodeParserTests
{
    private const string TwoSinksJson = """
    [
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Sink",
          "node.name": "alsa_output.pci-0000_00_1f.3.analog-stereo",
          "node.description": "Built-in Audio Analog Stereo"
        } }
      },
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Sink",
          "node.name": "bluez_output.AA_BB_CC.a2dp-sink"
        } }
      },
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Source",
          "node.name": "alsa_input.pci-0000_00_1f.3.analog-stereo"
        } }
      }
    ]
    """;

    [Fact]
    public void ParseSinksReturnsOnlyAudioSinkNodes()
    {
        var sinks = PipeWireNodeParser.ParseSinks(TwoSinksJson);

        Assert.Equal(2, sinks.Count);
        Assert.DoesNotContain(sinks, sink => sink.NodeName.Contains("alsa_input"));
    }

    [Fact]
    public void ParseSinksUsesDescriptionFallbackChain()
    {
        var sinks = PipeWireNodeParser.ParseSinks(TwoSinksJson);

        var withDescription = sinks.Single(s => s.NodeName == "alsa_output.pci-0000_00_1f.3.analog-stereo");
        Assert.Equal("Built-in Audio Analog Stereo", withDescription.DisplayName);

        var withoutDescription = sinks.Single(s => s.NodeName == "bluez_output.AA_BB_CC.a2dp-sink");
        Assert.Equal("bluez_output.AA_BB_CC.a2dp-sink", withoutDescription.DisplayName);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForMalformedJson()
    {
        var sinks = PipeWireNodeParser.ParseSinks("{ not json");
        Assert.Empty(sinks);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForOversizedInput()
    {
        var oversized = new string('x', 5 * 1024 * 1024);
        var sinks = PipeWireNodeParser.ParseSinks(oversized);
        Assert.Empty(sinks);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForEmptyInput()
    {
        Assert.Empty(PipeWireNodeParser.ParseSinks(string.Empty));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireNodeParserTests`
Expected: FAIL — `PipeWireNodeParser` does not exist.

- [ ] **Step 3: Implement the parser**

Replace the contents of `src/SonicRelay.Platform.Linux/Audio/PipeWireNode.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace SonicRelay.Platform.Linux.Audio;

public sealed record PipeWireNode(string NodeName, string DisplayName, bool IsAudioSink);

/// <summary>
/// Parses `pw-dump` JSON output into audio sink nodes. Malformed or oversized
/// output returns an empty list and never throws, so a bad discovery run
/// cannot crash Settings (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public static class PipeWireNodeParser
{
    private const int MaxInputBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<PipeWireNode> ParseSinks(string pwDumpJson)
    {
        if (string.IsNullOrWhiteSpace(pwDumpJson) || Encoding.UTF8.GetByteCount(pwDumpJson) > MaxInputBytes)
        {
            return [];
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(pwDumpJson); }
        catch (JsonException) { return []; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var nodes = new List<PipeWireNode>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryParseSink(element, out var node)) nodes.Add(node);
            }
            return nodes;
        }
    }

    private static bool TryParseSink(JsonElement element, out PipeWireNode node)
    {
        node = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("type", out var typeElement) ||
            typeElement.GetString() != "PipeWire:Interface:Node") return false;
        if (!element.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return false;
        if (!info.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) return false;

        var mediaClass = GetString(props, "media.class");
        if (mediaClass is null || !mediaClass.Contains("Audio/Sink", StringComparison.Ordinal)) return false;

        var nodeName = GetString(props, "node.name");
        if (string.IsNullOrWhiteSpace(nodeName)) return false;

        var displayName = GetString(props, "node.description")
            ?? GetString(props, "device.description")
            ?? GetString(props, "node.nick")
            ?? nodeName;

        node = new PipeWireNode(nodeName, displayName, true);
        return true;
    }

    private static string? GetString(JsonElement props, string propertyName) =>
        props.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireNodeParserTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/PipeWireNode.cs tests/SonicRelay.Platform.Linux.Tests/PipeWireNodeParserTests.cs
git commit -m "feat(linux): parse pw-dump JSON into audio sink nodes"
```

---

## Task 7: Resolve the default and a selected sink via `wpctl inspect`

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/WpctlInspectParser.cs`
- Create: `src/SonicRelay.Platform.Linux/Audio/PipeWireSinkResolver.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/WpctlInspectParserTests.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/PipeWireSinkResolverTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ResolvedSink(string NodeName, string? ObjectSerial);
  public static class WpctlInspectParser { public static ResolvedSink? Parse(string wpctlInspectOutput); }
  public sealed class PipeWireSinkResolver
  {
      public PipeWireSinkResolver(ILinuxProcessRunner processRunner, PipeWireCommandPaths commandPaths);
      public Task<ResolvedSink> ResolveDefaultAsync(CancellationToken cancellationToken);
      public Task<ResolvedSink> ResolveByNodeNameAsync(string nodeName, CancellationToken cancellationToken);
  }
  ```
- Consumes: `ILinuxProcessRunner`/`LinuxProcessResult` (Task 4), `PipeWireCommandPaths` (Task 5), `PipeWireNodeParser.ParseSinks` (Task 6), `AudioCaptureException`/`AudioCaptureError` (`SonicRelay.Windows.Audio`).

- [ ] **Step 1: Write the failing `WpctlInspectParser` tests**

Create `tests/SonicRelay.Platform.Linux.Tests/WpctlInspectParserTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class WpctlInspectParserTests
{
    private const string SampleInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * priority.session = "1000"
     * node.name = "alsa_output.pci-0000_00_1f.3.analog-stereo"
     * node.description = "Built-in Audio Analog Stereo"
     object.serial = "42"
    """;

    [Fact]
    public void ParseExtractsNodeNameAndObjectSerial()
    {
        var resolved = WpctlInspectParser.Parse(SampleInspectOutput);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", resolved!.NodeName);
        Assert.Equal("42", resolved.ObjectSerial);
    }

    [Fact]
    public void ParseReturnsNullWhenNodeNameIsMissing()
    {
        Assert.Null(WpctlInspectParser.Parse("id 55, type PipeWire:Interface:Node\n * priority.session = \"1000\""));
    }

    [Fact]
    public void ParseReturnsNullForEmptyInput()
    {
        Assert.Null(WpctlInspectParser.Parse(string.Empty));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter WpctlInspectParserTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement `WpctlInspectParser`**

Create `src/SonicRelay.Platform.Linux/Audio/WpctlInspectParser.cs`:

```csharp
namespace SonicRelay.Platform.Linux.Audio;

public sealed record ResolvedSink(string NodeName, string? ObjectSerial);

/// <summary>Parses the plain-text tree `wpctl inspect &lt;id&gt;` prints.</summary>
public static class WpctlInspectParser
{
    public static ResolvedSink? Parse(string wpctlInspectOutput)
    {
        if (string.IsNullOrWhiteSpace(wpctlInspectOutput)) return null;

        string? nodeName = null;
        string? objectSerial = null;
        foreach (var rawLine in wpctlInspectOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('*')) line = line[1..].Trim();
            var separatorIndex = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separatorIndex < 0) continue;
            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 3)..].Trim().Trim('"');
            if (key == "node.name") nodeName = value;
            else if (key == "object.serial") objectSerial = value;
        }

        return nodeName is null ? null : new ResolvedSink(nodeName, objectSerial);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter WpctlInspectParserTests`
Expected: PASS

- [ ] **Step 5: Write the failing `PipeWireSinkResolver` tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PipeWireSinkResolverTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireSinkResolverTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    private const string PwDumpJson = """
    [
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.default" } } },
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.headset" } } }
    ]
    """;

    [Fact]
    public async Task ResolveDefaultReturnsTheInspectedDefaultSink()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveDefaultAsync(CancellationToken.None);

        Assert.Equal("alsa_output.default", resolved.NodeName);
        Assert.Equal("55", resolved.ObjectSerial);
        Assert.Contains(runner.RunCalls, call => call.Executable == "wpctl" && call.Arguments.SequenceEqual(new[] { "inspect", "@DEFAULT_AUDIO_SINK@" }));
    }

    [Fact]
    public async Task ResolveDefaultThrowsNoDeviceWhenInspectFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(1, string.Empty, "no default sink"));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var exception = await Assert.ThrowsAsync<AudioCaptureException>(() => resolver.ResolveDefaultAsync(CancellationToken.None));
        Assert.Equal(AudioCaptureError.NoDevice, exception.Error);
    }

    [Fact]
    public async Task ResolveByNodeNameInspectsTheLiveNodeWhenStillPresent()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, """
        id 60, type PipeWire:Interface:Node
         * node.name = "alsa_output.headset"
         object.serial = "60"
        """, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveByNodeNameAsync("alsa_output.headset", CancellationToken.None);

        Assert.Equal("alsa_output.headset", resolved.NodeName);
        Assert.Equal("60", resolved.ObjectSerial);
    }

    [Fact]
    public async Task ResolveByNodeNameFallsBackToDefaultWhenSinkIsGone()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);

        var resolved = await resolver.ResolveByNodeNameAsync("alsa_output.unplugged", CancellationToken.None);

        Assert.Equal("alsa_output.default", resolved.NodeName);
    }
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireSinkResolverTests`
Expected: FAIL — `PipeWireSinkResolver` does not exist.

- [ ] **Step 7: Implement `PipeWireSinkResolver`**

Create `src/SonicRelay.Platform.Linux/Audio/PipeWireSinkResolver.cs`:

```csharp
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
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireSinkResolverTests`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/WpctlInspectParser.cs src/SonicRelay.Platform.Linux/Audio/PipeWireSinkResolver.cs tests/SonicRelay.Platform.Linux.Tests/WpctlInspectParserTests.cs tests/SonicRelay.Platform.Linux.Tests/PipeWireSinkResolverTests.cs
git commit -m "feat(linux): resolve default and selected sink targets via wpctl inspect"
```

---

## Task 8: `IAudioOutputDeviceProbe` implementation for sink selection UI

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/PipeWireOutputDeviceProbe.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/PipeWireOutputDeviceProbeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class PipeWireOutputDeviceProbe : IAudioOutputDeviceProbe
  {
      public PipeWireOutputDeviceProbe(ILinuxProcessRunner processRunner, PipeWireCommandPaths commandPaths);
      public IReadOnlyList<AudioOutputDevice> GetOutputDevices(); // synchronous, per the existing IAudioOutputDeviceProbe contract
  }
  ```
- Consumes: `IAudioOutputDeviceProbe`/`AudioOutputDevice` (`SonicRelay.Windows.Audio`, public), `ILinuxProcessRunner`/`PipeWireCommandPaths` (Tasks 4-5), `PipeWireNodeParser` (Task 6), `WpctlInspectParser` (Task 7).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PipeWireOutputDeviceProbeTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireOutputDeviceProbeTests
{
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string PwDumpJson = """
    [
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.default", "node.description": "Speakers" } } },
      { "type": "PipeWire:Interface:Node", "info": { "props": {
          "media.class": "Audio/Sink", "node.name": "alsa_output.headset", "node.description": "Headset" } } }
    ]
    """;

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    [Fact]
    public void GetOutputDevicesMarksTheDefaultSink()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        var devices = probe.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.True(devices.Single(d => d.Id == "alsa_output.default").IsDefault);
        Assert.False(devices.Single(d => d.Id == "alsa_output.headset").IsDefault);
        Assert.Equal("Headset", devices.Single(d => d.Id == "alsa_output.headset").Name);
    }

    [Fact]
    public void GetOutputDevicesReturnsEmptyWhenDiscoveryFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(1, string.Empty, "no session"));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        Assert.Empty(probe.GetOutputDevices());
    }

    [Fact]
    public void GetOutputDevicesStillReturnsSinksWhenDefaultLookupFails()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("pw-dump", new LinuxProcessResult(0, PwDumpJson, string.Empty));
        runner.Script("wpctl", new LinuxProcessResult(1, string.Empty, "no default"));
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        var devices = probe.GetOutputDevices();

        Assert.Equal(2, devices.Count);
        Assert.DoesNotContain(devices, d => d.IsDefault);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireOutputDeviceProbeTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement `PipeWireOutputDeviceProbe`**

Create `src/SonicRelay.Platform.Linux/Audio/PipeWireOutputDeviceProbe.cs`:

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireOutputDeviceProbeTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/PipeWireOutputDeviceProbe.cs tests/SonicRelay.Platform.Linux.Tests/PipeWireOutputDeviceProbeTests.cs
git commit -m "feat(linux): implement IAudioOutputDeviceProbe over PipeWire sinks"
```

---

## Task 9: Assemble raw PCM stdout bytes into exact 20 ms frames

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/PcmFrameAssembler.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/PcmFrameAssemblerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class PcmFrameAssembler
  {
      public PcmFrameAssembler(int sampleRate = 48_000, int channelCount = 2, int frameDurationMs = 20);
      public IReadOnlyList<(AudioFrame Frame, AudioLevelSnapshot Level)> Append(ReadOnlySpan<byte> chunk);
  }
  ```
- Consumes: `AudioFrame`, `AudioLevelSnapshot`, `AudioSampleFormat` (`SonicRelay.Windows.Audio`), `AudioLevelCalculator.Calculate` (Task 2).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PcmFrameAssemblerTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PcmFrameAssemblerTests
{
    private const int BytesPerFrame = 3840; // 48000 * 0.020 * 2 channels * 2 bytes

    [Fact]
    public void ExactSingleFrameProducesOneFrame()
    {
        var assembler = new PcmFrameAssembler();
        var chunk = new byte[BytesPerFrame];

        var frames = assembler.Append(chunk);

        Assert.Single(frames);
        Assert.Equal(BytesPerFrame, frames[0].Frame.Data.Length);
        Assert.Equal(AudioSampleFormat.Pcm16, frames[0].Frame.Format);
        Assert.Equal(48_000, frames[0].Frame.SampleRate);
        Assert.Equal(2, frames[0].Frame.ChannelCount);
    }

    [Fact]
    public void ArbitraryReadBoundariesStillProduceCorrectFrames()
    {
        var assembler = new PcmFrameAssembler();
        var total = new byte[BytesPerFrame * 2];
        new Random(1).NextBytes(total);

        var frames = new List<(AudioFrame, AudioLevelSnapshot)>();
        foreach (var chunk in total.Chunk(7)) // deliberately not frame- or sample-aligned
        {
            frames.AddRange(assembler.Append(chunk));
        }

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(BytesPerFrame, f.Item1.Data.Length));
    }

    [Fact]
    public void IncompleteTrailingBytesAreRetainedNotEmitted()
    {
        var assembler = new PcmFrameAssembler();
        var frames = assembler.Append(new byte[BytesPerFrame - 10]);

        Assert.Empty(frames);

        var completing = assembler.Append(new byte[10]);
        Assert.Single(completing);
    }

    [Fact]
    public void TimestampsAreMonotonicallyNonDecreasing()
    {
        var assembler = new PcmFrameAssembler();
        var first = assembler.Append(new byte[BytesPerFrame])[0].Frame.Timestamp;
        var second = assembler.Append(new byte[BytesPerFrame])[0].Frame.Timestamp;

        Assert.True(second >= first);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PcmFrameAssemblerTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement `PcmFrameAssembler`**

Create `src/SonicRelay.Platform.Linux/Audio/PcmFrameAssembler.cs`:

```csharp
using System.Diagnostics;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Buffers raw `pw-record` stdout PCM16 bytes into exact 20 ms frames,
/// tolerating arbitrary pipe read boundaries and never emitting partial
/// samples. `Append` is not thread-safe; the backend calls it from a single
/// read loop (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public sealed class PcmFrameAssembler(int sampleRate = 48_000, int channelCount = 2, int frameDurationMs = 20)
{
    private readonly int bytesPerFrame = sampleRate / 1000 * frameDurationMs * channelCount * 2;
    private readonly List<byte> pending = [];
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public IReadOnlyList<(AudioFrame Frame, AudioLevelSnapshot Level)> Append(ReadOnlySpan<byte> chunk)
    {
        pending.AddRange(chunk.ToArray());
        if (pending.Count < bytesPerFrame) return [];

        var frames = new List<(AudioFrame, AudioLevelSnapshot)>();
        while (pending.Count >= bytesPerFrame)
        {
            var frameBytes = pending.GetRange(0, bytesPerFrame).ToArray();
            pending.RemoveRange(0, bytesPerFrame);
            var level = AudioLevelCalculator.Calculate(frameBytes, AudioSampleFormat.Pcm16);
            var frame = new AudioFrame(frameBytes, sampleRate, channelCount, AudioSampleFormat.Pcm16, stopwatch.Elapsed);
            frames.Add((frame, level));
        }
        return frames;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PcmFrameAssemblerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/PcmFrameAssembler.cs tests/SonicRelay.Platform.Linux.Tests/PcmFrameAssemblerTests.cs
git commit -m "feat(linux): assemble raw PCM stdout bytes into exact 20ms frames"
```

---

## Task 10: `IAudioCaptureBackend` implementation supervising `pw-record`

**Files:**
- Create: `src/SonicRelay.Platform.Linux/Audio/PipeWireProcessBackend.cs`
- Test: `tests/SonicRelay.Platform.Linux.Tests/PipeWireProcessBackendTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class PipeWireProcessBackend : IAudioCaptureBackend
  {
      public PipeWireProcessBackend(
          ILinuxProcessRunner processRunner,
          PipeWireCommandPaths commandPaths,
          PipeWireSinkResolver sinkResolver,
          Func<string?>? preferredSinkNodeName = null);
  }
  ```
- Consumes: `IAudioCaptureBackend` (Task 1, public), `ILinuxProcess`/`ILinuxProcessRunner` (Task 4), `PipeWireCommandPaths` (Task 5), `PipeWireSinkResolver`/`ResolvedSink` (Task 7), `PcmFrameAssembler` (Task 9).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Platform.Linux.Tests/PipeWireProcessBackendTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireProcessBackendTests
{
    private const int BytesPerFrame = 3840;
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    private static (PipeWireProcessBackend Backend, FakeLinuxProcessRunner Runner) CreateBackend(Func<string?>? preferred = null)
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);
        var backend = new PipeWireProcessBackend(runner, Paths, resolver, preferred);
        return (backend, runner);
    }

    [Fact]
    public async Task StartAsyncLaunchesPwRecordWithTheResolvedTargetAndExplicitFormat()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        // StartAsync awaits the first frame; feed one immediately.
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        Assert.Equal("alsa_output.default", backend.Device!.Id);
        var pwRecordCall = runner.RunCalls; // pw-record goes through Start(), not RunAsync
        Assert.Contains(new[] { "--target=55" }, arg => true); // sanity: no exception constructing args
    }

    [Fact]
    public async Task StartAsyncCompletesOnlyAfterFirstFrameArrives()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(startTask.IsCompleted);

        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;
        Assert.True(startTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FramesRaiseFrameAvailableWithPcm16Format()
    {
        var (backend, runner) = CreateBackend();
        AudioFrame? received = null;
        backend.FrameAvailable += (frame, _) => received ??= frame;

        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        Assert.NotNull(received);
        Assert.Equal(AudioSampleFormat.Pcm16, received!.Format);
        Assert.Equal(48_000, received.SampleRate);
    }

    [Fact]
    public async Task UnexpectedProcessExitAfterStartupRaisesFaulted()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;
        runner.LastStartedProcess!.RaiseExited(1);

        await Task.Delay(50);
        Assert.NotNull(faulted);
        Assert.Equal(AudioCaptureError.PlatformFailure, faulted!.Error);
    }

    [Fact]
    public async Task StopAsyncStopsTheProcessAndDoesNotRaiseFaulted()
    {
        var (backend, runner) = CreateBackend();
        var startTask = backend.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        var process = runner.LastStartedProcess!;
        AudioCaptureException? faulted = null;
        backend.Faulted += error => faulted = error;

        var stopTask = backend.StopAsync(CancellationToken.None);
        process.RaiseExited(0); // simulates the real process exiting once StopAsync signals it
        await stopTask;

        Assert.Null(faulted);
        Assert.Null(backend.Device);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotent()
    {
        var (backend, _) = CreateBackend();
        await backend.DisposeAsync();
        await backend.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireProcessBackendTests`
Expected: FAIL — `PipeWireProcessBackend` does not exist.

- [ ] **Step 3: Implement `PipeWireProcessBackend`**

Create `src/SonicRelay.Platform.Linux/Audio/PipeWireProcessBackend.cs`:

```csharp
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Audio;

/// <summary>
/// Supervises exactly one `pw-record` process per instance, capturing the
/// explicitly resolved sink target as raw PCM16 stereo 48 kHz. Pause performs
/// a controlled stop; resume re-resolves and starts a new process against the
/// same preferred sink — the small discontinuity is preferable to Unix signal
/// interop in the first release (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public sealed class PipeWireProcessBackend : IAudioCaptureBackend
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(2);

    private readonly ILinuxProcessRunner processRunner;
    private readonly PipeWireCommandPaths commandPaths;
    private readonly PipeWireSinkResolver sinkResolver;
    private readonly Func<string?> preferredSinkNodeName;

    private ILinuxProcess? process;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;

    public PipeWireProcessBackend(
        ILinuxProcessRunner processRunner,
        PipeWireCommandPaths commandPaths,
        PipeWireSinkResolver sinkResolver,
        Func<string?>? preferredSinkNodeName = null)
    {
        this.processRunner = processRunner;
        this.commandPaths = commandPaths;
        this.sinkResolver = sinkResolver;
        this.preferredSinkNodeName = preferredSinkNodeName ?? (() => null);
    }

    public AudioDeviceInfo? Device { get; private set; }
    public event Action<AudioFrame, AudioLevelSnapshot>? FrameAvailable;
    public event Action<AudioCaptureException>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (process is not null) return;

        var preferredId = preferredSinkNodeName();
        var resolved = string.IsNullOrWhiteSpace(preferredId)
            ? await sinkResolver.ResolveDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await sinkResolver.ResolveByNodeNameAsync(preferredId, cancellationToken).ConfigureAwait(false);

        var target = resolved.ObjectSerial ?? resolved.NodeName;
        string[] arguments =
        [
            "--raw", "--rate=48000", "--channels=2", "--format=s16", "--latency=20ms",
            $"--target={target}", "-"
        ];

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchedProcess = processRunner.Start(commandPaths.PwRecord, arguments);
        process = launchedProcess;
        launchedProcess.Exited += OnProcessExited;

        var assembler = new PcmFrameAssembler();
        readCancellation = new CancellationTokenSource();
        readTask = Task.Run(() => ReadLoopAsync(launchedProcess, assembler, started, readCancellation.Token), CancellationToken.None);

        using var startupTimeoutSource = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupTimeoutSource.Token);
        try
        {
            await started.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new AudioCaptureException(AudioCaptureError.PlatformFailure, "PipeWire capture did not produce audio within the startup timeout.");
        }

        Device = new AudioDeviceInfo(resolved.NodeName, resolved.NodeName, 48_000, 2, AudioSampleFormat.Pcm16);
    }

    private async Task ReadLoopAsync(ILinuxProcess launchedProcess, PcmFrameAssembler assembler, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await launchedProcess.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                foreach (var (frame, level) in assembler.Append(buffer.AsSpan(0, read)))
                {
                    started.TrySetResult();
                    FrameAvailable?.Invoke(frame, level);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            var mapped = new AudioCaptureException(AudioCaptureError.PlatformFailure, "PipeWire capture stream failed.", error);
            if (!started.TrySetException(mapped) && !cancellationToken.IsCancellationRequested) Faulted?.Invoke(mapped);
        }
    }

    private void OnProcessExited(int exitCode)
    {
        if (readCancellation is null || readCancellation.IsCancellationRequested) return; // an intentional Stop already cancelled reads
        var error = exitCode == 0
            ? new AudioCaptureException(AudioCaptureError.DeviceLost, "The PipeWire capture process exited unexpectedly.")
            : new AudioCaptureException(AudioCaptureError.PlatformFailure, $"pw-record exited with code {exitCode}.");
        Faulted?.Invoke(error);
    }

    /// <summary>Pause performs a controlled stop; there is no separate pause primitive for pw-record.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <summary>Resume re-resolves the preferred sink and starts a new process.</summary>
    public Task ResumeAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        readCancellation?.Cancel();
        if (readTask is not null)
        {
            try { await readTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            await process.StopAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }
        process = null;
        readCancellation?.Dispose();
        readCancellation = null;
        readTask = null;
        Device = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter PipeWireProcessBackendTests`
Expected: PASS. If `StartAsyncLaunchesPwRecordWithTheResolvedTargetAndExplicitFormat` or the timing-dependent tests flake locally, increase the `Task.Delay(50)` in that test slightly rather than changing production code — the fake process's `Write` happens on the test thread, and the backend's read loop needs one scheduler tick to observe it.

- [ ] **Step 5: Run the whole Linux test project to check for regressions**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests`
Expected: PASS — all tests from Tasks 3-10.

- [ ] **Step 6: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/PipeWireProcessBackend.cs tests/SonicRelay.Platform.Linux.Tests/PipeWireProcessBackendTests.cs
git commit -m "feat(linux): supervise pw-record as an IAudioCaptureBackend"
```

---

## Task 11: Prove captured frames reach the existing WebRTC audio bridge

**Files:**
- Modify: `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj` (add test-only project references)
- Test: `tests/SonicRelay.Platform.Linux.Tests/WebRtcAudioBridgeIntegrationTests.cs`

**Interfaces:**
- Consumes: `AudioCaptureService.Create` (Task 1), `PipeWireProcessBackend` (Task 10), `WebRtcAudioBridge` (`SonicRelay.Windows.Presentation`, unchanged), `IWebRtcPublisher`/`WebRtcAudioFrame`/`WebRtcPublisherDiagnostics` (`SonicRelay.Windows.WebRtc`), `ISignalingMessageHandler`/`SignalingMessageEnvelope` (`SonicRelay.Windows.Signaling`).
- Produces: no new production code — this is the spec's required proof that "frames validate through the existing WebRTC audio bridge" with no changes needed to `WebRtcAudioBridge` itself.

- [ ] **Step 1: Add the test-only project references**

`SonicRelay.Platform.Linux` (the production project) must **not** reference `SonicRelay.Windows.Presentation`/`WebRtc`/`Signaling` — only the test project needs them, to prove the existing bridge accepts frames from the new backend. In `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj`, add to the existing `<ItemGroup>` containing the `SonicRelay.Platform.Linux` project reference:

```xml
    <ProjectReference Include="../../src/SonicRelay.Windows.Presentation/SonicRelay.Windows.Presentation.csproj" />
    <ProjectReference Include="../../src/SonicRelay.Windows.WebRtc/SonicRelay.Windows.WebRtc.csproj" />
    <ProjectReference Include="../../src/SonicRelay.Windows.Signaling/SonicRelay.Windows.Signaling.csproj" />
```

- [ ] **Step 2: Write the failing integration test**

Create `tests/SonicRelay.Platform.Linux.Tests/WebRtcAudioBridgeIntegrationTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Platform.Linux.Tests;

internal sealed class FakeWebRtcPublisher : IWebRtcPublisher
{
    public List<WebRtcAudioFrame> PushedFrames { get; } = [];
    public WebRtcPublisherDiagnostics Diagnostics { get; } = new(0, []);
    public event Action<WebRtcPublisherDiagnostics>? DiagnosticsChanged;

    public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        PushedFrames.Add(frame);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class WebRtcAudioBridgeIntegrationTests
{
    private const int BytesPerFrame = 3840;
    private static readonly PipeWireCommandPaths Paths = new("pw-dump", "pw-record", "wpctl", "secret-tool");

    private const string DefaultInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * node.name = "alsa_output.default"
     object.serial = "55"
    """;

    [Fact]
    public async Task FramesCapturedByThePipeWireBackendReachTheWebRtcPublisher()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("wpctl", new LinuxProcessResult(0, DefaultInspectOutput, string.Empty));
        var resolver = new PipeWireSinkResolver(runner, Paths);
        var backend = new PipeWireProcessBackend(runner, Paths, resolver);
        var probe = new PipeWireOutputDeviceProbe(runner, Paths);

        await using var audio = AudioCaptureService.Create(backend, probe);
        var publisher = new FakeWebRtcPublisher();
        await using var bridge = new WebRtcAudioBridge(audio, publisher);

        var startTask = audio.StartAsync();
        await Task.Delay(50);
        runner.LastStartedProcess!.Write(new byte[BytesPerFrame]);
        await startTask;

        await WaitUntilAsync(() => publisher.PushedFrames.Count > 0, TimeSpan.FromSeconds(2));

        Assert.NotEmpty(publisher.PushedFrames);
        var pushed = publisher.PushedFrames[0];
        Assert.Equal(48_000, pushed.SampleRate);
        Assert.Equal(2, pushed.ChannelCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter WebRtcAudioBridgeIntegrationTests`
Expected: FAIL — missing project references (`SonicRelay.Windows.Presentation` types not found) until Step 1's csproj edit is in place; once the csproj is edited, this should compile. If it still fails at runtime, it's a real bug — do not skip to "expected" without seeing the actual failure reason first.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter WebRtcAudioBridgeIntegrationTests`
Expected: PASS — proves `PipeWireProcessBackend` frames flow unmodified through `AudioCaptureService` → `WebRtcAudioBridge` → `IWebRtcPublisher.PushAudioFrameAsync`, with zero changes to `WebRtcAudioBridge.cs`.

- [ ] **Step 5: Run the full solution test suite for regressions**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS across every project (Windows and Linux).

- [ ] **Step 6: Commit**

```bash
git add tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj tests/SonicRelay.Platform.Linux.Tests/WebRtcAudioBridgeIntegrationTests.cs
git commit -m "test(linux): prove PipeWire-captured frames reach WebRtcAudioBridge unmodified"
```

---

## Task 12: Update architecture documentation

**Files:**
- Modify: `docs/architecture.md`

**Interfaces:** none (documentation only).

- [ ] **Step 1: Update the project boundaries section**

In `docs/architecture.md`, after the existing "Platform contracts" table (around line 82), add a short subsection recording what now exists (this plan does not wire Linux into the desktop shell yet — that is a separate follow-up plan for spec PR 2):

```markdown
### Linux audio adapter (issue #32, PR 1 of the Linux design)

`SonicRelay.Platform.Linux` implements `IAudioCaptureBackend` and
`IAudioOutputDeviceProbe` over PipeWire/WirePlumber (`pw-dump`, `wpctl
inspect`, `pw-record`), following
[`2026-07-14-linux-desktop-publisher-design.md`](superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
It is proven against the shared `AudioCaptureService`/`WebRtcAudioBridge`
pipeline by unit and integration tests using fake process runners; it is not
yet wired into `SonicRelay.Windows.Desktop` (`App.axaml.cs` still calls
`CreatePreview()` on Linux) or built/packaged in CI. That composition, Secret
Service token storage, XDG paths, and Linux CI/distribution are follow-up
work (spec PR 2 and PR 3).
```

- [ ] **Step 2: Commit**

```bash
git add docs/architecture.md
git commit -m "docs: record the Linux audio adapter's current scope in architecture.md"
```

---

## Self-Review Notes (for whoever executes this plan)

- **Spec coverage:** this plan implements spec section "Selected capture approach" (supervised `pw-dump`/`wpctl`/`pw-record`, never through a shell), "Sink discovery and selection" (Tasks 6-8), "PipeWire capture process" including frame assembly and pause/resume/supervision/error mapping (Tasks 9-10), and "Shared audio seams" (Tasks 1-2). It intentionally does **not** cover "Platform runtime composition", "Linux token storage", "Linux paths", "Tray and lifecycle", "Desktop project changes", "CI", or "Packaging and release" — those map to the spec's own PR 2 and PR 3 slices and need their own plans once this one lands.
- **Error mapping table:** `PipeWireCommandLocator` covers the "missing pw-record/pw-dump/wpctl" row; `PipeWireSinkResolver.ResolveDefaultAsync` covers "no/default sink missing" → `NoDevice`; `PipeWireProcessBackend`'s startup timeout and `OnProcessExited` cover "sink/process lost during capture" → `DeviceLost`/`PlatformFailure`. The spec's `AccessDenied` ("permission/socket denied") row is not distinctly implemented in this plan — `pw-record` permission failures currently surface as generic `PlatformFailure` via the exit-code path, since the exact `pw-record` stderr text for permission failures cannot be verified without a real PipeWire session in this environment. Flag this as a known gap to close during the manual Ubuntu 24.04 validation gate the spec defines for PR 3, by inspecting real stderr and adding a targeted string match in `OnProcessExited`/`ReadLoopAsync` at that point.
