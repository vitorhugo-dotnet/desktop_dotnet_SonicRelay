# Linux Desktop Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement PR 2 of the Linux desktop publisher design (issue #32, sub-issue #39): wire the already-merged `SonicRelay.Platform.Linux` PipeWire audio adapter into the real Avalonia desktop shell, add Secret-Service-backed Linux token storage with an in-memory fallback, and stop falling back to `MainWindowViewModel.CreatePreview()` on Linux.

**Architecture:** A new `DesktopRuntimeFactory` in `SonicRelay.Windows.Desktop` is the platform composition root: on Windows it composes the existing WASAPI + DPAPI path unchanged; on Linux it composes `PipeWireProcessBackend` + `PipeWireOutputDeviceProbe` (from PR 1) with a new `SecretServiceTokenStore`/`InMemoryTokenStore` (`ITokenStore`, from `SonicRelay.Windows.Core`). `PublisherRuntime.Create` gains two optional, backward-compatible parameters (`ITokenStore?`, `AudioOutputPreferenceStore?`) so the composition root can inject platform-specific storage without changing the existing Windows call site. `App.axaml.cs` calls the factory instead of branching on `OperatingSystem.IsWindows()`.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit 2.9.3, Avalonia (existing), `System.Text.Json` (BCL).

## Global Constraints

- Target `net10.0`, `LangVersion 14.0`, `Nullable enable`, `ImplicitUsings enable` — matches every existing project.
- No `Directory.Packages.props`; each `.csproj` declares its own `<PackageReference>` versions directly. This plan adds no new third-party packages.
- Test framework is xUnit: `coverlet.collector 6.0.4`, `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.4`.
- Solution file is `.slnx`; no new projects are added in this plan (only new files/project references in existing projects).
- Never invoke a shell for `secret-tool`; always `ProcessStartInfo.ArgumentList` via the existing `ILinuxProcessRunner` (never `/bin/sh -c`).
- Never place token contents in process arguments, environment variables, or logs; the secret is written to `secret-tool`'s stdin only. Never create a plaintext token file as a fallback — the fallback is in-memory only.
- Existing Windows behavior and public APIs must not regress: `PublisherRuntime.Create(Uri, IAudioCaptureService)` (the existing two-argument call) must keep compiling and behaving identically; `AudioCaptureService`'s public constructor and `Create` factory (from the Linux audio adapter plan) are unchanged.
- This repo's CI runs on `windows-latest` only today (a Linux matrix is a separate, later phase — issue #39's sibling sub-issue #40); nothing in this plan may add a test that only passes on Linux without a runtime OS guard, mirroring the fix already applied to `LinuxProcessRunnerTests.cs`.
- Manual, real-desktop validation (Ubuntu 24.04, real PipeWire session, real Secret Service, real tray) is explicitly out of scope for this plan — it requires hardware/a live desktop this environment does not have. Everything in this plan is scoped to what is unit-testable with fakes, consistent with how the Linux audio adapter plan (`docs/superpowers/plans/2026-07-15-linux-audio-adapter.md`) handled the same constraint.

---

## File Structure

```
src/SonicRelay.Platform.Linux/
  Audio/LinuxProcessRunner.cs                MODIFY — RunAsync gains optional stdin support
  Storage/
    InMemoryTokenStore.cs                    NEW — ITokenStore, in-memory fallback
    SecretServiceTokenStore.cs               NEW — ITokenStore via secret-tool
  SonicRelay.Platform.Linux.csproj           MODIFY — add ProjectReference to SonicRelay.Windows.Core

tests/SonicRelay.Platform.Linux.Tests/
  Fakes/FakeLinuxProcessRunner.cs            MODIFY — record stdin in RunCalls
  LinuxProcessRunnerTests.cs                 MODIFY — add a stdin round-trip test (Linux-guarded, real process)
  Storage/
    InMemoryTokenStoreTests.cs               NEW
    SecretServiceTokenStoreTests.cs          NEW
  SonicRelay.Platform.Linux.Tests.csproj     MODIFY — add ProjectReference to SonicRelay.Windows.Core

src/SonicRelay.Windows.Presentation/
  PublisherRuntime.cs                        MODIFY — optional ITokenStore/AudioOutputPreferenceStore overrides, expose TokenStore

tests/SonicRelay.Windows.Presentation.Tests/
  PublisherRuntimeTests.cs                   NEW

src/SonicRelay.Windows.Desktop/
  DesktopTrayController.cs                   MODIFY — fix subscription-ordering bug
  DesktopRuntimeFactory.cs                   NEW — Windows/Linux/unsupported composition root
  App.axaml.cs                               MODIFY — call the factory; drop the Linux CreatePreview() fallback

docs/architecture.md                         MODIFY — record PR 2 completion and remaining PR 3 scope
```

---

## Task 1: Support writing to stdin for one-shot `ILinuxProcessRunner.RunAsync` calls

**Files:**
- Modify: `src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs`
- Modify: `tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs`
- Modify: `tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs`

**Interfaces:**
- Produces: `ILinuxProcessRunner.RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)` — the new trailing optional parameter. `FakeLinuxProcessRunner.RunCalls` becomes `List<(string Executable, IReadOnlyList<string> Arguments, string? StandardInput)>` (existing call sites that access `.Executable`/`.Arguments` by name are unaffected by the new third element).
- Consumes: nothing new. This is a leaf change other tasks in this plan build on (Task 2's `SecretServiceTokenStore` needs to write the token payload to `secret-tool store`'s stdin).

- [ ] **Step 1: Write the failing tests**

Add to `tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs` (inside the existing `LinuxProcessRunnerTests` class, alongside the other `OperatingSystem.IsLinux()`-guarded real-process tests):

```csharp
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
```

Update `tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs`'s `RunCalls` type and `RunAsync` signature to match the new interface member (shown in full in Step 3 below, since the fake must compile against the interface change before the tests above can even build).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter LinuxProcessRunnerTests`
Expected: FAIL to compile — `RunAsync` has no `standardInput` parameter yet, and `FakeLinuxProcessRunner` won't match the interface until Step 3.

- [ ] **Step 3: Update the fake alongside the interface**

Replace `tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs`'s `FakeLinuxProcessRunner` class body (keep `FakeLinuxProcess` unchanged):

```csharp
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
```

- [ ] **Step 4: Implement the interface and real runner change**

In `src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs`, change the interface member:

```csharp
public interface ILinuxProcessRunner
{
    Task<LinuxProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null);

    ILinuxProcess Start(string executable, IReadOnlyList<string> arguments);
}
```

Change `LinuxProcessRunner.RunAsync`'s signature and body — insert the stdin write/close between `BeginErrorReadLine()` and the timeout-wait block:

```csharp
    public async Task<LinuxProcessResult> RunAsync(
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

        return new LinuxProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
```

Everything else in the file (`Start`, `BuildStartInfo`, `KillTree`, `LinuxProcess`) is unchanged.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter LinuxProcessRunnerTests`
Expected: PASS — this sandbox is Linux, so the two new `OperatingSystem.IsLinux()`-guarded tests actually execute here (not just no-op).

- [ ] **Step 6: Run the full Linux test project to check for regressions**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests`
Expected: PASS, same total count as before plus 2 (every existing caller of `RunAsync`/`RunCalls` compiles unchanged because the new parameter is optional and the new tuple element is additive).

- [ ] **Step 7: Commit**

```bash
git add src/SonicRelay.Platform.Linux/Audio/LinuxProcessRunner.cs tests/SonicRelay.Platform.Linux.Tests/Fakes/FakeLinuxProcessRunner.cs tests/SonicRelay.Platform.Linux.Tests/LinuxProcessRunnerTests.cs
git commit -m "feat(linux): support writing stdin in ILinuxProcessRunner.RunAsync"
```

---

## Task 2: Linux token storage — `SecretServiceTokenStore` and `InMemoryTokenStore`

**Files:**
- Modify: `src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj`
- Modify: `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj`
- Create: `src/SonicRelay.Platform.Linux/Storage/InMemoryTokenStore.cs`
- Create: `src/SonicRelay.Platform.Linux/Storage/SecretServiceTokenStore.cs`
- Create: `tests/SonicRelay.Platform.Linux.Tests/Storage/InMemoryTokenStoreTests.cs`
- Create: `tests/SonicRelay.Platform.Linux.Tests/Storage/SecretServiceTokenStoreTests.cs`

**Interfaces:**
- Produces: `public sealed class InMemoryTokenStore : ITokenStore`; `public sealed class SecretServiceTokenStore(ILinuxProcessRunner processRunner, string secretToolPath) : ITokenStore`. Both live in namespace `SonicRelay.Platform.Linux.Storage`.
- Consumes: `ITokenStore`, `TokenSet`, `TokenStorageResult`, `TokenStorageStatus` from `SonicRelay.Windows.Core.Storage` (public, existing) — requires the new project reference below. `ILinuxProcessRunner`/`LinuxProcessResult` from Task 1 (unchanged interface shape aside from the new optional parameter).

- [ ] **Step 1: Add the project references**

In `src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj`, add to the existing `<ItemGroup>`:

```xml
    <ProjectReference Include="../SonicRelay.Windows.Core/SonicRelay.Windows.Core.csproj" />
```

In `tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj`, add to the existing `<ItemGroup>` (alongside the `SonicRelay.Platform.Linux` reference — the `SonicRelay.Windows.Presentation`/`WebRtc`/`Signaling` test-only references from the Linux audio adapter plan's Task 11 already transitively pull in Core, but an explicit reference keeps the dependency honest):

```xml
    <ProjectReference Include="../../src/SonicRelay.Windows.Core/SonicRelay.Windows.Core.csproj" />
```

- [ ] **Step 2: Write the failing `InMemoryTokenStore` tests**

Create `tests/SonicRelay.Platform.Linux.Tests/Storage/InMemoryTokenStoreTests.cs`:

```csharp
using SonicRelay.Platform.Linux.Storage;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Tests.Storage;

public sealed class InMemoryTokenStoreTests
{
    [Fact]
    public async Task LoadReturnsNoTokensBeforeAnySave()
    {
        var store = new InMemoryTokenStore();
        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTheTokens()
    {
        var store = new InMemoryTokenStore();
        var tokens = new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1));

        var saveResult = await store.SaveAsync(tokens);
        var loadResult = await store.LoadAsync();

        Assert.True(saveResult.Succeeded);
        Assert.Equal(tokens, loadResult.Tokens);
    }

    [Fact]
    public async Task DeleteClearsTheStoredTokens()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var deleteResult = await store.DeleteAsync();
        var loadResult = await store.LoadAsync();

        Assert.True(deleteResult.Succeeded);
        Assert.Null(loadResult.Tokens);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter InMemoryTokenStoreTests`
Expected: FAIL — `InMemoryTokenStore` doesn't exist.

- [ ] **Step 4: Implement `InMemoryTokenStore`**

Create `src/SonicRelay.Platform.Linux/Storage/InMemoryTokenStore.cs`:

```csharp
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Storage;

/// <summary>
/// Session-only token storage for when Secret Service is unavailable. Never
/// creates a plaintext file (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
/// "Linux token storage" — fallback). Tokens are lost on process exit, so the
/// user must sign in again after restarting SonicRelay.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private TokenSet? tokens;

    public Task<TokenStorageResult> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        this.tokens = tokens;
        return Task.FromResult(TokenStorageResult.Success());
    }

    public Task<TokenStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TokenStorageResult.Success(tokens));

    public Task<TokenStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        tokens = null;
        return Task.FromResult(TokenStorageResult.Success());
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter InMemoryTokenStoreTests`
Expected: PASS

- [ ] **Step 6: Write the failing `SecretServiceTokenStore` tests**

Create `tests/SonicRelay.Platform.Linux.Tests/Storage/SecretServiceTokenStoreTests.cs`:

```csharp
using System.Text.Json;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Storage;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Tests.Storage;

public sealed class SecretServiceTokenStoreTests
{
    private static readonly TokenSet SampleTokens = new("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task SaveWritesTheSerializedTokensToStandardInputNeverToArguments()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, string.Empty, string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.SaveAsync(SampleTokens);

        Assert.True(result.Succeeded);
        Assert.Single(runner.RunCalls);
        var call = runner.RunCalls[0];
        Assert.Equal("secret-tool", call.Executable);
        Assert.Equal("store", call.Arguments[0]);
        Assert.DoesNotContain(call.Arguments, arg => arg.Contains(SampleTokens.AccessToken, StringComparison.Ordinal));
        Assert.DoesNotContain(call.Arguments, arg => arg.Contains(SampleTokens.RefreshToken, StringComparison.Ordinal));
        Assert.NotNull(call.StandardInput);
        var roundTripped = JsonSerializer.Deserialize<TokenSet>(call.StandardInput!);
        Assert.Equal(SampleTokens, roundTripped);
    }

    [Fact]
    public async Task SaveMapsANonZeroExitCodeToSecureStorageUnavailable()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "keyring locked"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.SaveAsync(SampleTokens);

        Assert.Equal(TokenStorageStatus.SecureStorageUnavailable, result.Status);
    }

    [Fact]
    public async Task LoadReturnsTheStoredTokensOnSuccess()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, JsonSerializer.Serialize(SampleTokens), string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(SampleTokens, result.Tokens);
    }

    [Fact]
    public async Task LoadTreatsANonZeroExitCodeAsNoStoredSessionNotAFailure()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "no matching secret"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task LoadMapsMalformedStoredDataToFailed()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, "not json", string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.Equal(TokenStorageStatus.Failed, result.Status);
    }

    [Fact]
    public async Task DeleteSucceedsEvenWhenNothingWasStored()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "no matching secret"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.DeleteAsync();

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task NeverLogsOrArgumentsTheSecretToolAttributesWithTokenContent()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, string.Empty, string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        await store.SaveAsync(SampleTokens);
        await store.LoadAsync();
        await store.DeleteAsync();

        foreach (var call in runner.RunCalls)
        {
            Assert.All(call.Arguments, arg => Assert.DoesNotContain(SampleTokens.AccessToken, arg, StringComparison.Ordinal));
        }
    }
}
```

- [ ] **Step 7: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter SecretServiceTokenStoreTests`
Expected: FAIL — `SecretServiceTokenStore` doesn't exist.

- [ ] **Step 8: Implement `SecretServiceTokenStore`**

Create `src/SonicRelay.Platform.Linux/Storage/SecretServiceTokenStore.cs`:

```csharp
using System.Text.Json;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Storage;

/// <summary>
/// Persists tokens via `secret-tool` (Secret Service). Fixed attributes identify
/// the entry; the secret is always provided on stdin, never as an argument or
/// logged. Unavailable/locked Secret Service maps to SecureStorageUnavailable so
/// the caller can fall back to session-only storage (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
/// "Linux token storage" — ADR-LINUX-007).
/// </summary>
public sealed class SecretServiceTokenStore(ILinuxProcessRunner processRunner, string secretToolPath) : ITokenStore
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Attributes = ["application", "sonicrelay", "purpose", "publisher-token"];

    public async Task<TokenStorageResult> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string[] arguments = ["store", "--label=SonicRelay publisher token", .. Attributes];
        var payload = JsonSerializer.Serialize(tokens);
        var result = await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken, standardInput: payload).ConfigureAwait(false);
        return result.ExitCode == 0
            ? TokenStorageResult.Success()
            : TokenStorageResult.SecureStorageUnavailable("Secret Service is unavailable or locked.");
    }

    public async Task<TokenStorageResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        string[] arguments = ["lookup", .. Attributes];
        var result = await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        // A non-zero exit here means "no matching secret", not a broken Secret
        // Service — that is simply "no stored session", the same as a missing
        // token file on Windows.
        if (result.ExitCode != 0) return TokenStorageResult.Success();

        try
        {
            var tokens = JsonSerializer.Deserialize<TokenSet>(result.StandardOutput.TrimEnd('\n'));
            return tokens is null
                ? TokenStorageResult.Failed("Stored token data is invalid.")
                : TokenStorageResult.Success(tokens);
        }
        catch (JsonException)
        {
            return TokenStorageResult.Failed("Stored token data is invalid.");
        }
    }

    public async Task<TokenStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        string[] arguments = ["clear", .. Attributes];
        // secret-tool clear exits non-zero when nothing was stored; that is not
        // a failure from the caller's point of view (there is nothing to delete).
        await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        return TokenStorageResult.Success();
    }
}
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests --filter SecretServiceTokenStoreTests`
Expected: PASS

- [ ] **Step 10: Run the full Linux test project to check for regressions**

Run: `dotnet test tests/SonicRelay.Platform.Linux.Tests`
Expected: PASS, previous total plus 9 new tests (3 `InMemoryTokenStoreTests` + 7 `SecretServiceTokenStoreTests`, though re-count exactly from the actual run rather than trusting this arithmetic).

- [ ] **Step 11: Commit**

```bash
git add src/SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj tests/SonicRelay.Platform.Linux.Tests/SonicRelay.Platform.Linux.Tests.csproj src/SonicRelay.Platform.Linux/Storage tests/SonicRelay.Platform.Linux.Tests/Storage
git commit -m "feat(linux): add Secret Service token store with in-memory fallback"
```

---

## Task 3: Let `PublisherRuntime.Create` accept platform-specific token/output-preference storage

**Files:**
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`
- Create: `tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs`

**Interfaces:**
- Produces: `public static PublisherRuntime Create(Uri backendBaseUrl, IAudioCaptureService audioCapture, ITokenStore? tokenStoreOverride = null, AudioOutputPreferenceStore? audioOutputPreferenceOverride = null)` (both new parameters optional and trailing — the existing two-argument call site in `App.axaml.cs` keeps compiling unchanged until Task 6). New public property `public ITokenStore TokenStore { get; }`.
- Consumes: `ITokenStore`, `AudioOutputPreferenceStore` (both already public, from `SonicRelay.Windows.Core`).

- [ ] **Step 1: Write the failing tests**

Create `tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs`:

```csharp
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherRuntimeTests
{
    private static readonly Uri BackendUrl = new("https://backend.example.test/");

    [Fact]
    public async Task CreateWithoutOverridesUsesTheDefaultWindowsTokenStore()
    {
        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio());

        Assert.IsType<UserScopedTokenStore>(runtime.TokenStore);
    }

    [Fact]
    public async Task CreateWithATokenStoreOverrideUsesItInstead()
    {
        var tokenStore = new InMemoryFakeTokenStore();

        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio(), tokenStoreOverride: tokenStore);

        Assert.Same(tokenStore, runtime.TokenStore);
    }

    [Fact]
    public async Task CreateWithAnAudioOutputPreferenceOverrideExposesTheSameInstance()
    {
        var preference = new AudioOutputPreferenceStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "audio-output.json"));

        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio(), audioOutputPreferenceOverride: preference);

        Assert.Same(preference, runtime.AudioOutput);
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public AudioCaptureState State => AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics { get; } = new(AudioCaptureState.Stopped, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId => null;
        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured;
        public event Action<AudioLevelSnapshot>? LevelChanged;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryFakeTokenStore : ITokenStore
    {
        public Task<TokenStorageResult> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default) => Task.FromResult(TokenStorageResult.Success());
        public Task<TokenStorageResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TokenStorageResult.Success());
        public Task<TokenStorageResult> DeleteAsync(CancellationToken cancellationToken = default) => Task.FromResult(TokenStorageResult.Success());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests --filter PublisherRuntimeTests`
Expected: FAIL to compile — `Create` has no `tokenStoreOverride`/`audioOutputPreferenceOverride` parameters yet, and `PublisherRuntime.TokenStore` doesn't exist.

- [ ] **Step 3: Implement the changes**

In `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`:

Add a new field and constructor parameter, and a public property, alongside the existing ones (private ctor at line 33 and the properties block after it):

```csharp
    private readonly HttpClient httpClient;
    private readonly IPeerConnectionManager peers;
    private readonly IWebRtcPublisher webRtcPublisher;
    private readonly WebRtcAudioBridge audioBridge;
    private string? lastLoggedState;
    private bool hadActiveSession;

    private PublisherRuntime(
        HttpClient httpClient,
        PublisherWorkflow workflow,
        Uri backendBaseUrl,
        IPeerConnectionManager peers,
        IWebRtcPublisher webRtcPublisher,
        WebRtcAudioBridge audioBridge,
        RelayPreferenceStore relayPreference,
        AudioQualityStore audioQuality,
        IAudioCaptureService audioCapture,
        AudioOutputPreferenceStore audioOutput,
        DiagnosticLog diagnosticLog,
        ITokenStore tokenStore)
    {
        this.httpClient = httpClient;
        this.peers = peers;
        this.webRtcPublisher = webRtcPublisher;
        this.audioBridge = audioBridge;
        Workflow = workflow;
        BackendBaseUrl = backendBaseUrl;
        RelayPreference = relayPreference;
        AudioQuality = audioQuality;
        AudioCapture = audioCapture;
        AudioOutput = audioOutput;
        DiagnosticLog = diagnosticLog;
        TokenStore = tokenStore;
        ReportExporter = new DiagnosticReportExporter();
        Workflow.StateChanged += OnWorkflowStateChanged;
        _ = WriteDiagnosticAsync("runtime", "Publisher runtime configured.", new Dictionary<string, string>
        {
            ["backend"] = DiagnosticRedactor.BackendHost(backendBaseUrl)
        });
    }

    public PublisherWorkflow Workflow { get; }
    public Uri BackendBaseUrl { get; }
    public RelayPreferenceStore RelayPreference { get; }
    public AudioQualityStore AudioQuality { get; }
    public IAudioCaptureService AudioCapture { get; }
    public AudioOutputPreferenceStore AudioOutput { get; }
    public DiagnosticLog DiagnosticLog { get; }
    public DiagnosticReportExporter ReportExporter { get; }
    public ITokenStore TokenStore { get; }
    public IWebRtcPublisher WebRtcPublisher => webRtcPublisher;
```

Change the `Create` factory signature and the two internal composition lines that build `tokenStore`/`audioOutput`, and the final `return new PublisherRuntime(...)` call:

```csharp
    /// <summary>
    /// Composes the shared publisher runtime for one backend. The platform shell
    /// supplies its capture implementation (WASAPI loopback on Windows, PipeWire on
    /// Linux — issue #32) and, optionally, its own token store and audio-output
    /// preference store (Linux uses Secret Service instead of DPAPI); omitting
    /// either keeps the existing Windows-default behavior.
    /// </summary>
    public static PublisherRuntime Create(
        Uri backendBaseUrl,
        IAudioCaptureService audioCapture,
        ITokenStore? tokenStoreOverride = null,
        AudioOutputPreferenceStore? audioOutputPreferenceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(backendBaseUrl);
        ArgumentNullException.ThrowIfNull(audioCapture);
        if (!backendBaseUrl.IsAbsoluteUri || backendBaseUrl.Scheme is not ("http" or "https"))
            throw new ConfigurationValidationException("Backend URL must be an absolute HTTP or HTTPS URL.");

        var normalized = backendBaseUrl.AbsoluteUri.EndsWith('/') ? backendBaseUrl : new Uri(backendBaseUrl.AbsoluteUri + "/");
        var signalingUrl = new Uri(normalized, "ws/signaling");
        var configuration = new PublisherConfiguration(normalized, signalingUrl, 4);
        configuration.Validate();
        var tokenStore = tokenStoreOverride ?? new UserScopedTokenStore();
        var http = new HttpClient { BaseAddress = normalized, Timeout = TimeSpan.FromSeconds(30) };
```

(the rest of the method body up to `var audioOutput = ...` is unchanged — only that one line changes:)

```csharp
        var audio = audioCapture;
        var audioOutput = audioOutputPreferenceOverride ?? new AudioOutputPreferenceStore();
```

And the final return statement gains the new `tokenStore` argument:

```csharp
        return new PublisherRuntime(http, workflow, normalized, peers, webRtcPublisher, audioBridge, relayPreference, audioQuality, audio, audioOutput, diagnosticLog, tokenStore);
```

Everything else in `Create` (ICE servers provider, signaling client, peer connection manager, workflow construction) is unchanged — note it already uses the local variable `tokenStore` for `SignalingClient`, `WebRtcApiClient`, `AuthApiClient`, `DeviceApiClient`, `SessionApiClient`; those call sites need no edits since the local variable name is unchanged, only its initializer.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests --filter PublisherRuntimeTests`
Expected: PASS

- [ ] **Step 5: Run the full Presentation test project to check for regressions**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests`
Expected: PASS, previous total plus 3 new tests. `PublisherWorkflowTests.cs`'s own `FakeAudio : IAudioCaptureService` is a separate `private sealed class` scoped to that file — no collision with the new one in `PublisherRuntimeTests.cs`.

- [ ] **Step 6: Commit**

```bash
git add src/SonicRelay.Windows.Presentation/PublisherRuntime.cs tests/SonicRelay.Windows.Presentation.Tests/PublisherRuntimeTests.cs
git commit -m "feat(desktop): let PublisherRuntime.Create accept a token store and audio-output preference override"
```

---

## Task 4: Fix a tray-subscription-ordering bug and log tray availability

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/DesktopTrayController.cs`
- Modify: `src/SonicRelay.Windows.Desktop/App.axaml.cs`

**Interfaces:** none new — this is a bug fix in existing, already-shipped code plus one new diagnostic log line. No new public members.

**Context for the implementer:** `DesktopTrayController`'s constructor currently wires `window.Closing`/`window.PropertyChanged`/`viewModel.Changed` handlers *before* calling `TrayIcon.SetIcons(...)`, which is the call that actually registers the icon with the platform tray backend and is the most likely thing to fail on a Linux desktop environment without tray protocol support. If `SetIcons` throws after those handlers are already wired, the constructor's exception is caught by `App.axaml.cs`'s try/catch (so `tray` is never assigned and `desktop.Exit` never disposes it) — but the event subscriptions on the real, live `window` object were already made and are never unwound, keeping the half-constructed `DesktopTrayController` instance alive. If the app later decides to hide-to-tray (an active session is running when the user closes the window), `OnWindowClosing` still fires on this leaked instance, hides the window, and there is no visible tray icon to bring it back — an unreachable hidden process. This directly contradicts the design spec's tray requirement ("without tray, closing exits instead of leaving an unreachable hidden process" — `docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md`, "Tray and lifecycle"). The fix is to register the icon with the platform backend *first*, before any subscription — if that throws, nothing has been wired yet and the constructor exits cleanly.

- [ ] **Step 1: Apply the fix**

In `src/SonicRelay.Windows.Desktop/DesktopTrayController.cs`, reorder the constructor body (currently lines 38-51) from:

```csharp
        trayIcon = new TrayIcon
        {
            Icon = TryCreateIcon(),
            ToolTipText = controller.TooltipFor(viewModel.CurrentSnapshot),
            IsVisible = true,
        };
        trayIcon.Clicked += (_, _) => ShowWindow();

        viewModel.Changed += Refresh;
        window.Closing += OnWindowClosing;
        window.PropertyChanged += OnWindowPropertyChanged;

        Refresh();
        TrayIcon.SetIcons(Application.Current!, [trayIcon]);
    }
```

to:

```csharp
        trayIcon = new TrayIcon
        {
            Icon = TryCreateIcon(),
            ToolTipText = controller.TooltipFor(viewModel.CurrentSnapshot),
            IsVisible = true,
        };

        // Register with the platform tray backend before wiring any subscriptions
        // below: if the desktop environment has no tray support, this throws, and
        // the constructor must leave nothing subscribed — otherwise a half-constructed
        // instance stays alive via a dangling window.Closing/PropertyChanged handler,
        // and a later "hide to tray" decision hides the window with no visible tray
        // icon to bring it back (an unreachable hidden process — see
        // docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
        // "Tray and lifecycle").
        TrayIcon.SetIcons(Application.Current!, [trayIcon]);

        trayIcon.Clicked += (_, _) => ShowWindow();
        viewModel.Changed += Refresh;
        window.Closing += OnWindowClosing;
        window.PropertyChanged += OnWindowPropertyChanged;

        Refresh();
    }
```

- [ ] **Step 2: Log tray availability from `App.axaml.cs`**

In `src/SonicRelay.Windows.Desktop/App.axaml.cs`, change the tray construction try/catch (currently):

```csharp
            try
            {
                var tray = new DesktopTrayController(desktop, mainWindow, viewModel);
                desktop.Exit += (_, _) => tray.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
```

to:

```csharp
            try
            {
                var tray = new DesktopTrayController(desktop, mainWindow, viewModel);
                desktop.Exit += (_, _) => tray.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                viewModel.LogDiagnostic("tray", "Tray integration unavailable; closing the window will exit normally instead of minimizing to tray.");
            }
```

`MainWindowViewModel.LogDiagnostic(string, string)` already exists and is used the same way inside `DesktopTrayController` itself (e.g. `viewModel.LogDiagnostic("window-state", "Window close intercepted; kept running in tray.")`), so this matches an established pattern exactly.

- [ ] **Step 3: Verify — build and run the existing suite**

There is no dedicated `DesktopTrayControllerTests.cs` in the repo today, and simulating a real Avalonia `TrayIcon.SetIcons` failure from a unit test is not reliable (it depends on platform tray backend behavior this environment cannot exercise). Verify this change by:

Run: `dotnet build SonicRelay.Windows.slnx`
Expected: 0 errors.

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests`
Expected: PASS, same total as before (this is a pure reordering plus one new diagnostic call; no existing test constructs `DesktopTrayController` directly, confirmed by `grep -rn DesktopTrayController tests/`).

- [ ] **Step 4: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/DesktopTrayController.cs src/SonicRelay.Windows.Desktop/App.axaml.cs
git commit -m "fix(desktop): register the tray icon before wiring window subscriptions"
```

---

## Task 5: `DesktopRuntimeFactory` — Windows/Linux/unsupported composition root

**Files:**
- Create: `src/SonicRelay.Windows.Desktop/DesktopRuntimeFactory.cs`

**Interfaces:**
- Produces: `public static class DesktopRuntimeFactory { public static PublisherRuntime? Create(Uri backendBaseUrl); }` — returns `null` on a platform that is neither Windows nor Linux (explicit unsupported-platform state, matching the design spec).
- Consumes: `AudioCaptureService` (public ctor, Windows-only, from `SonicRelay.Windows.Audio`), `PublisherRuntime.Create` (Task 3's new signature), and from `SonicRelay.Platform.Linux`: `PipeWireCommandLocator`, `LinuxProcessRunner`, `PipeWireSinkResolver`, `PipeWireOutputDeviceProbe`, `PipeWireProcessBackend`, `SecretServiceTokenStore`, `InMemoryTokenStore` (all public, from the merged Linux audio adapter plan plus Task 2 of this plan). `AudioOutputPreferenceStore` from `SonicRelay.Windows.Core.Configuration`. `AudioCaptureService.Create` (public factory) from `SonicRelay.Windows.Audio`.

**No automated test for this file.** Its Linux branch composes real OS-facing objects (`new LinuxProcessRunner()`, `PipeWireCommandLocator().Locate()` which calls `Environment.GetEnvironmentVariable("PATH")`/`File.Exists` for real tools) with no injection seam — the constituent pieces it wires together are already fully unit-tested in `SonicRelay.Platform.Linux.Tests` (48+ tests from the merged Linux audio adapter plan, plus Task 2's new token-store tests), and this repo's CI only runs `windows-latest` today, so the Linux branch would never execute there anyway. A dedicated test would either be a fragile environment-dependent smoke test or would require adding an injection seam whose only consumer is the test itself — not worth it for a thin composition-root file. This mirrors how `App.axaml.cs`'s own composition logic has no direct unit test in this repo.

- [ ] **Step 1: Implement the factory**

Create `src/SonicRelay.Windows.Desktop/DesktopRuntimeFactory.cs`:

```csharp
using System.Runtime.Versioning;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Storage;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop;

/// <summary>
/// Platform composition root for the publisher runtime (issue #32): Windows
/// composes WASAPI capture with DPAPI token storage (unchanged); Linux composes
/// the PipeWire adapter with Secret Service token storage (falling back to an
/// in-memory store when Secret Service is unavailable). Any other platform is an
/// explicit unsupported state, not a silent preview.
/// </summary>
public static class DesktopRuntimeFactory
{
    public static PublisherRuntime? Create(Uri backendBaseUrl)
    {
        if (OperatingSystem.IsWindows()) return CreateWindows(backendBaseUrl);
        if (OperatingSystem.IsLinux()) return CreateLinux(backendBaseUrl);
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static PublisherRuntime CreateWindows(Uri backendBaseUrl) =>
        PublisherRuntime.Create(backendBaseUrl, new AudioCaptureService());

    private static PublisherRuntime CreateLinux(Uri backendBaseUrl)
    {
        var commandPaths = new PipeWireCommandLocator().Locate();
        var processRunner = new LinuxProcessRunner();
        var resolver = new PipeWireSinkResolver(processRunner, commandPaths);
        var probe = new PipeWireOutputDeviceProbe(processRunner, commandPaths);
        var audioOutputPreference = new AudioOutputPreferenceStore();
        var backend = new PipeWireProcessBackend(processRunner, commandPaths, resolver, () => audioOutputPreference.SelectedDeviceId);
        var audioCapture = AudioCaptureService.Create(backend, probe);

        ITokenStore tokenStore = commandPaths.SecretTool is { } secretToolPath
            ? new SecretServiceTokenStore(processRunner, secretToolPath)
            : new InMemoryTokenStore();

        return PublisherRuntime.Create(backendBaseUrl, audioCapture, tokenStore, audioOutputPreference);
    }
}
```

`PipeWireCommandLocator().Locate()` throws `AudioCaptureException` when `pw-dump`/`pw-record`/`wpctl` are missing — this propagates up to `App.axaml.cs`'s existing `try`/`catch` around runtime attachment (Task 6), consistent with how any other startup failure there is already handled today (stay on the sign-in surface, retry later).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build SonicRelay.Windows.slnx`
Expected: 0 errors. Note `src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj` does not yet reference `SonicRelay.Platform.Linux` — check the current `<ItemGroup>` of project references and add `<ProjectReference Include="../SonicRelay.Platform.Linux/SonicRelay.Platform.Linux.csproj" />` if it's missing (compare against the other `<ProjectReference>` entries already there for `SonicRelay.Windows.Presentation` etc. to match the relative-path style used).

- [ ] **Step 3: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/DesktopRuntimeFactory.cs src/SonicRelay.Windows.Desktop/SonicRelay.Windows.Desktop.csproj
git commit -m "feat(desktop): add DesktopRuntimeFactory to compose the Windows or Linux runtime"
```

---

## Task 6: Wire `DesktopRuntimeFactory` into `App.axaml.cs`; drop the Linux `CreatePreview()` fallback

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/App.axaml.cs`

**Interfaces:** none new. This task only changes which branch conditions gate existing calls.

**Context for the implementer:** `MainWindowViewModel.CreatePreview()` must keep existing for the Avalonia designer and explicit visual tests (`tests/SonicRelay.Windows.Desktop.Tests/ShellRenderTests.cs`, `MainWindowViewModelStateTests.cs` both call it directly, independent of `App.axaml.cs` — confirmed via `grep -rn CreatePreview tests/`) — do not remove or rename it. Only stop `App.axaml.cs` from choosing it on Linux.

- [ ] **Step 1: Apply the change**

In `src/SonicRelay.Windows.Desktop/App.axaml.cs`, replace the whole file with:

```csharp
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Desktop.Views;

namespace SonicRelay.Windows.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // On Windows and Linux the shell attaches a live publisher runtime (real
            // capture + the configured backend) and opens on the sign-in surface until
            // a session is restored or the user signs in. Elsewhere — the headless
            // render tests, or an unsupported OS — the shell opens on a representative
            // preview so the layout and design system stay verifiable (issue #32).
            var viewModel = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? new MainWindowViewModel()
                : MainWindowViewModel.CreatePreview();

            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            // Tray + minimize/close-to-tray + reconnect (issue #32). Never let a missing tray
            // backend stop the app from launching.
            try
            {
                var tray = new DesktopTrayController(desktop, mainWindow, viewModel);
                desktop.Exit += (_, _) => tray.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                viewModel.LogDiagnostic("tray", "Tray integration unavailable; closing the window will exit normally instead of minimizing to tray.");
            }

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                _ = AttachConfiguredRuntimeAsync(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task AttachConfiguredRuntimeAsync(MainWindowViewModel viewModel)
    {
        try
        {
            var configuration = await new UserConfigurationLoader().LoadAsync();
            var runtime = DesktopRuntimeFactory.Create(configuration.BackendBaseUrl);
            if (runtime is null) return; // unsupported platform: stay on the sign-in surface
            viewModel.Attach(runtime);
            // Restore a persisted session (refresh + /auth/me) so a returning user lands on the
            // dashboard; a missing/expired session simply leaves the sign-in surface showing.
            await runtime.Workflow.RestoreSessionAsync();
        }
        catch
        {
            // Backend unreachable, no stored session, or a missing platform capture
            // dependency (e.g. PipeWire tools not installed) at startup: stay on the
            // sign-in surface so the user can retry once the condition is resolved.
        }
    }
}
```

Key differences from the current file: the `[SupportedOSPlatform("windows")]` attribute is removed from `AttachConfiguredRuntimeAsync` (it no longer directly constructs a Windows-only type — `DesktopRuntimeFactory.Create` does that internally, behind its own `[SupportedOSPlatform("windows")]`-attributed private helper), both `OperatingSystem.IsWindows()` guards become `OperatingSystem.IsWindows() || OperatingSystem.IsLinux()`, the runtime-construction line changes from `PublisherRuntime.Create(configuration.BackendBaseUrl, new AudioCaptureService())` to `DesktopRuntimeFactory.Create(configuration.BackendBaseUrl)` with a null check, and the `using SonicRelay.Windows.Audio;` import is no longer needed (removed) since this file no longer references `AudioCaptureService` directly.

- [ ] **Step 2: Build and verify no platform-compatibility warnings**

Run: `dotnet build SonicRelay.Windows.slnx`
Expected: 0 errors, and no new `CA1416` (platform-compatibility) warnings introduced by this file — `DesktopRuntimeFactory.CreateWindows` is the only place that still constructs `new AudioCaptureService()`, and it is correctly attributed.

- [ ] **Step 3: Run the full Desktop test project**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests`
Expected: PASS, same total as before — no existing test drives `App.OnFrameworkInitializationCompleted` (confirmed by the earlier research: `TestAppBuilder.cs` builds `App` headlessly for Skia rendering setup, but individual tests construct `MainWindowViewModel`/`MainWindow` directly, bypassing this method entirely).

- [ ] **Step 4: Run the full solution test suite**

Run: `dotnet test SonicRelay.Windows.slnx`
Expected: PASS across all 8 projects, no regressions anywhere.

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/App.axaml.cs
git commit -m "feat(desktop): attach the real Linux runtime instead of falling back to CreatePreview()"
```

---

## Task 7: Update architecture documentation

**Files:**
- Modify: `docs/architecture.md`

**Interfaces:** none (documentation only).

- [ ] **Step 1: Update the Linux audio adapter section**

In `docs/architecture.md`, find the "Linux audio adapter (issue #32, PR 1 of the Linux design)" subsection (added by the Linux audio adapter plan) and replace its body with an updated version reflecting PR 2's completion:

```markdown
### Linux desktop composition (issue #32, PR 2 of the Linux design)

`DesktopRuntimeFactory` (`src/SonicRelay.Windows.Desktop/DesktopRuntimeFactory.cs`) is
the platform composition root: on Windows it composes WASAPI capture with DPAPI
token storage (unchanged); on Linux it composes `SonicRelay.Platform.Linux`'s
`PipeWireProcessBackend`/`PipeWireOutputDeviceProbe` with a new Secret-Service-backed
`SecretServiceTokenStore` (falling back to an in-memory, session-only store when
Secret Service is unavailable — never a plaintext file). `App.axaml.cs` now attaches
a real runtime on both platforms instead of falling back to
`MainWindowViewModel.CreatePreview()` on Linux; `CreatePreview()` remains only for
the Avalonia designer and headless render tests.

Not yet covered: XDG-specific config/state/cache directory layout (the existing
`Environment.SpecialFolder.LocalApplicationData`-based paths already resolve
correctly on Linux via .NET's BCL, so this was not blocking), user-visible
actionable startup error messaging when a Linux capture dependency is missing
(today it silently falls back to the sign-in surface, matching existing Windows
behavior — not a regression, but short of the design spec's "actionable platform
error" ask), and — as before — Linux CI/packaging/distribution, both tracked in a
separate follow-up (spec PR 3, issue #40). Manual, real-desktop validation (Ubuntu
24.04, real PipeWire session, real Secret Service, real tray) has not been
performed in this environment and remains the release gate per the design spec.
```

- [ ] **Step 2: Commit**

```bash
git add docs/architecture.md
git commit -m "docs: record the Linux desktop composition's current scope in architecture.md"
```

---

## Self-Review Notes (for whoever executes this plan)

- **Spec coverage:** this plan implements the design spec's "PR 2 — Desktop composition" bullets that are unit-testable without a live desktop: `DesktopRuntimeFactory`, token/runtime dependency injection, Secret Service plus in-memory fallback, attaching the real Linux runtime instead of `CreatePreview()`. It does **not** implement: XDG config/state/cache directory taxonomy (deliberately dropped — the existing cross-platform-safe paths already work, see Task 7's doc note), the `ISystemTrayService`/`IWindowLifecycleService`/`INotificationService`/`IPlatformPermissionService`/`IAutoStartService` contracts (confirmed to exist as unused, unimplemented scaffolding from an earlier phase — `DesktopTrayController` already talks to Avalonia's cross-platform `TrayIcon` directly and works today; implementing these dead interfaces is out of scope, not a requirement this plan's goal depends on), and the manual Ubuntu 24.04 validation checklist (requires real hardware/desktop this environment does not have).
- **A design correction versus the literal spec:** the original spec sketched a `DesktopRuntimeDependencies` record and a `PublisherRuntime.Create(Uri, DesktopRuntimeDependencies)` signature. This plan instead adds two optional, backward-compatible parameters to the existing `Create(Uri, IAudioCaptureService)` signature. Reasoning: introducing a new record type and breaking the existing signature would touch every existing caller/test for no functional gain — the actual requirement (Linux needs a different `ITokenStore` and must share one `AudioOutputPreferenceStore` instance between the capture backend and the runtime) is fully satisfied by two optional parameters, and the existing Windows call site keeps compiling unchanged.
- **A real bug found and fixed along the way, not requested by the original spec text:** `DesktopTrayController`'s constructor wired window/viewModel event subscriptions *before* the call that actually registers the tray icon with the platform backend, so a failed tray registration on Linux could leave dangling subscriptions on a half-constructed instance — leading to exactly the "unreachable hidden process" the design spec warns against. Fixed in Task 4 by reordering; see that task's rationale.
