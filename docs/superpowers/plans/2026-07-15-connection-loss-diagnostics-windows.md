# Connection-Loss Diagnostics (Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Windows publisher a bounded, exportable, clearable diagnostic log that captures exactly what happened around a connection loss (minimize/close-to-tray, signaling close reason, reconnect attempts, ICE restarts).

**Architecture:** `DiagnosticLog` (Core) gains retention cleanup, `ClearAsync`, and `ExportAsync`. `SignalingClient` and `WebRtcPublisher` gain narrow new events carrying bounded reason/attempt data. `PublisherRuntime` subscribes to those events and writes them through the existing `WriteDiagnosticAsync` wrapper. `MainWindowViewModel` gets `ExportDiagnosticsCommand`/`ClearDiagnosticsCommand` (same `RelayCommand` pattern as existing commands), and `DiagnosticsView.axaml` is rewired so those commands are bindable from the Diagnostics page.

**Tech Stack:** .NET 10, Avalonia (Desktop), xUnit.

## Global Constraints

- Retention default: 3 days (delete `publisher-*.jsonl` files older than that on `DiagnosticLog` construction).
- No new dialog/confirmation infrastructure — Clear uses an arm/confirm double-click pattern on the existing `RelayCommand`, matching the codebase's existing no-confirmation convention for destructive actions.
- All new event/reason data must be bounded (fixed enums/strings), never raw exception messages, matching `DiagnosticRedactor`'s existing guarantee.
- Diagnostics must never throw into or interrupt publisher/UI operation — every new diagnostic write is wrapped the same way `PublisherRuntime.WriteDiagnosticAsync` already wraps writes.

---

### Task 1: `DiagnosticLog` retention, `ClearAsync`, `ExportAsync`

**Files:**
- Modify: `src/SonicRelay.Windows.Core/Diagnostics/DiagnosticLog.cs`
- Test: `tests/SonicRelay.Windows.Core.Tests/DiagnosticLogRetentionTests.cs` (new file)

**Interfaces:**
- Produces: `DiagnosticLog.ClearAsync(CancellationToken ct = default): Task`, `DiagnosticLog.ExportAsync(CancellationToken ct = default): Task<string>` (returns the exported file's path). Both are consumed by `MainWindowViewModel` in Task 6.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SonicRelay.Windows.Core.Tests/DiagnosticLogRetentionTests.cs
using SonicRelay.Windows.Core.Diagnostics;

namespace SonicRelay.Windows.Core.Tests;

public sealed class DiagnosticLogRetentionTests
{
    [Fact]
    public async Task ConstructionDeletesFilesOlderThanRetentionAndKeepsNewerOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var oldPath = Path.Combine(directory, "publisher-20200101.jsonl");
            var newPath = Path.Combine(directory, $"publisher-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(oldPath, "{}\n");
            await File.WriteAllTextAsync(newPath, "{}\n");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-10));

            _ = new DiagnosticLog(directory, retention: TimeSpan.FromDays(3));

            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(newPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClearAsyncDeletesLogFilesAndEmptiesRecentEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            await log.WriteAsync("auth", "signed in");
            Assert.Single(log.RecentEvents);
            Assert.True(File.Exists(log.LogPath));

            await log.ClearAsync();

            Assert.Empty(log.RecentEvents);
            Assert.False(File.Exists(log.LogPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClearAsyncDropsAWriteThatWasAlreadyQueuedPastTheInMemoryUpdate()
    {
        // Regression test for the race a Codex review caught on PR #37: WriteAsync must not
        // update RecentEvents before it holds writeLock, otherwise a write queued behind an
        // in-flight ClearAsync can still land on disk (and back in memory) right after the clear.
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            await log.WriteAsync("auth", "first"); // establishes the file

            var blocker = log.WriteAsync("auth", "second"); // held up only by real I/O timing in practice
            await log.ClearAsync();
            await blocker;

            // After both complete, either the clear fully preceded the second write (file exists,
            // contains only "second") or fully followed it (file absent) — never a state where
            // RecentEvents is empty but the file still contains "second" from a write that
            // "escaped" the clear via the old before-the-lock update ordering.
            var recentContainsSecond = log.RecentEvents.Any(e => e.Message == "second");
            var fileContainsSecond = File.Exists(log.LogPath) && (await File.ReadAllTextAsync(log.LogPath)).Contains("second", StringComparison.Ordinal);
            Assert.Equal(recentContainsSecond, fileContainsSecond);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExportAsyncConcatenatesRetainedFilesIntoOneFileAndReturnsItsPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            await log.WriteAsync("auth", "first");
            await log.WriteAsync("auth", "second");

            var exportedPath = await log.ExportAsync();

            Assert.True(File.Exists(exportedPath));
            Assert.StartsWith(directory, exportedPath, StringComparison.Ordinal);
            var content = await File.ReadAllTextAsync(exportedPath);
            Assert.Contains("first", content, StringComparison.Ordinal);
            Assert.Contains("second", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Core.Tests --filter DiagnosticLogRetentionTests`
Expected: FAIL — `DiagnosticLog` has no `retention` constructor parameter, no `ClearAsync`, no `ExportAsync`.

- [ ] **Step 3: Implement retention, the lock fix, `ClearAsync`, and `ExportAsync`**

Replace the full contents of `src/SonicRelay.Windows.Core/Diagnostics/DiagnosticLog.cs`:

```csharp
using System.Text.Json;

namespace SonicRelay.Windows.Core.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    string Category,
    string Message,
    IReadOnlyDictionary<string, string> Properties);

public sealed class DiagnosticLog : IDisposable
{
    private const int EventLimit = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly List<DiagnosticEvent> recentEvents = [];
    private readonly string directory;

    public DiagnosticLog(string? directory = null, TimeSpan? retention = null)
    {
        this.directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRelay", "WindowsPublisher", "logs");
        LogPath = Path.Combine(this.directory, $"publisher-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        DeleteExpiredFiles(retention ?? TimeSpan.FromDays(3));
    }

    public string LogPath { get; }
    public IReadOnlyList<DiagnosticEvent> RecentEvents
    {
        get { lock (recentEvents) return recentEvents.ToArray(); }
    }

    public async Task WriteAsync(
        string category,
        string message,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        var safeProperties = (properties ?? new Dictionary<string, string>())
            .ToDictionary(
                pair => DiagnosticRedactor.Redact(pair.Key),
                pair => DiagnosticRedactor.IsSensitiveKey(pair.Key) ? "[REDACTED]" : DiagnosticRedactor.Redact(pair.Value));
        var item = new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            DiagnosticRedactor.Redact(category),
            DiagnosticRedactor.Redact(message),
            safeProperties);

        // Both the in-memory update and the disk append happen under writeLock so that
        // ClearAsync — which also holds writeLock — can never race a write past the point
        // where it's already visible in RecentEvents but not yet flushed to disk (or vice
        // versa). See DiagnosticLogRetentionTests for the regression this fixes.
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            lock (recentEvents)
            {
                recentEvents.Add(item);
                if (recentEvents.Count > EventLimit) recentEvents.RemoveRange(0, recentEvents.Count - EventLimit);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            await File.AppendAllTextAsync(LogPath, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Deletes every retained log file and empties the in-memory event buffer.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in EnumerateLogFiles())
            {
                TryDelete(file);
            }
            lock (recentEvents) recentEvents.Clear();
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// Concatenates every retained log file (oldest first) into one exported file and
    /// returns its path. Lines are already redacted at write time, so this is a plain
    /// byte-level concatenation — no further sanitization is needed.
    /// </summary>
    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var exportPath = Path.Combine(directory, $"sonicrelay-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
            var temporaryPath = exportPath + ".tmp";
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write))
            {
                foreach (var file in EnumerateLogFiles())
                {
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
            File.Move(temporaryPath, exportPath, true);
            return exportPath;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private void DeleteExpiredFiles(TimeSpan retention)
    {
        try
        {
            var cutoff = DateTime.UtcNow - retention;
            foreach (var file in EnumerateLogFiles())
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention cleanup must never stop the publisher from starting.
        }
    }

    private IEnumerable<string> EnumerateLogFiles() =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "publisher-*.jsonl") : [];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => writeLock.Dispose();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Core.Tests --filter "DiagnosticLogRetentionTests|DiagnosticReportTests"`
Expected: PASS (all `DiagnosticLogRetentionTests` cases, and the pre-existing `DiagnosticReportTests.DiagnosticLogWritesOnlyRedactedJsonLines` keeps passing since `WriteAsync`'s external behavior is unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Windows.Core/Diagnostics/DiagnosticLog.cs tests/SonicRelay.Windows.Core.Tests/DiagnosticLogRetentionTests.cs
git commit -m "Add DiagnosticLog retention, ClearAsync, and ExportAsync"
```

---

### Task 2: `SignalingClient` close-reason and reconnect-attempt events

**Files:**
- Modify: `src/SonicRelay.Windows.Signaling/SignalingClient.cs`
- Modify: `src/SonicRelay.Windows.Signaling/ISignalingClient.cs` (add the new events to the interface)
- Test: `tests/SonicRelay.Windows.Signaling.Tests/SignalingClientTests.cs`

**Interfaces:**
- Produces: `ISignalingClient.Closed: event Action<SignalingCloseReason>?`, `ISignalingClient.ReconnectAttempting: event Action<int>?` (attempt index, 0-based), and the enum `SignalingCloseReason { NormalClosure, SessionEnded, ReconnectExhausted, SessionGone }`. Consumed by `PublisherRuntime` in Task 4.

- [ ] **Step 1: Write the failing tests**

Append to `tests/SonicRelay.Windows.Signaling.Tests/SignalingClientTests.cs` (inside the `SignalingClientTests` class, e.g. after `ReconnectJitterNeverPushesTheDelayBelowZeroOrAboveMaxDelay`):

```csharp
    [Fact]
    public async Task ReconnectAttemptingFiresOnceForEachAttemptBeforeItsDelay()
    {
        var initial = new FakeWebSocketConnection();
        var replacement = new FakeWebSocketConnection();
        var factory = new FakeWebSocketFactory(initial, replacement);
        var delay = new ImmediateReconnectDelay();
        var attempts = new List<int>();
        await using var client = CreateClient(factory, delay: delay);
        client.ReconnectAttempting += attempts.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1", "device-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => factory.CreatedCount == 2 && client.State == SignalingConnectionState.Connected, timeout.Token);

        Assert.Equal([0], attempts);
    }

    [Fact]
    public async Task ClosedFiresWithNormalClosureAfterAnExplicitClose()
    {
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(new FakeWebSocketFactory(new FakeWebSocketConnection()));
        client.Closed += reasons.Add;
        await client.ConnectAsync("session-1", "device-1");

        await client.CloseAsync();

        Assert.Equal([SignalingCloseReason.NormalClosure], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithSessionEndedWhenTheServerEndsTheSession()
    {
        var socket = new FakeWebSocketConnection();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(new FakeWebSocketFactory(socket));
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1", "device-1");

        socket.QueueText(new SignalingMessageEnvelope(SignalingMessageTypes.SessionEnded, "session-1"));
        await WaitUntilAsync(() => reasons.Count > 0, timeout.Token);

        Assert.Equal([SignalingCloseReason.SessionEnded], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithReconnectExhaustedAfterMaxAttempts()
    {
        var initial = new FakeWebSocketConnection();
        var failure = new WebSocketException("transient");
        var factory = new FakeWebSocketFactory(
            initial,
            new FakeWebSocketConnection { ConnectException = failure });
        var delay = new ImmediateReconnectDelay();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(factory, delay: delay, policy: new SignalingReconnectPolicy { MaxAttempts = 1 });
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1", "device-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Faulted, timeout.Token);

        Assert.Equal([SignalingCloseReason.ReconnectExhausted], reasons);
    }

    [Fact]
    public async Task ClosedFiresWithSessionGoneWhenTheBackendReportsTheSessionIsGone()
    {
        var initial = new FakeWebSocketConnection();
        var gone = new FakeWebSocketConnection { ConnectException = new SignalingSessionGoneException(HttpStatusCode.Gone) };
        var factory = new FakeWebSocketFactory(initial, gone);
        var delay = new ImmediateReconnectDelay();
        var reasons = new List<SignalingCloseReason>();
        await using var client = CreateClient(factory, delay: delay);
        client.Closed += reasons.Add;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("session-1", "device-1");

        initial.QueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await WaitUntilAsync(() => client.State == SignalingConnectionState.Closed, timeout.Token);

        Assert.Equal([SignalingCloseReason.SessionGone], reasons);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Signaling.Tests --filter "ReconnectAttemptingFiresOnceForEachAttemptBeforeItsDelay|ClosedFiresWith"`
Expected: FAIL to compile — `SignalingClient`/`ISignalingClient` has no `ReconnectAttempting`, `Closed`, or `SignalingCloseReason`.

- [ ] **Step 3: Add the enum, interface events, and firing logic**

In `src/SonicRelay.Windows.Signaling/ISignalingClient.cs`, add two events to the interface (alongside the existing `StateChanged`):

```csharp
event Action<int>? ReconnectAttempting;
event Action<SignalingCloseReason>? Closed;
```

In `src/SonicRelay.Windows.Signaling/SignalingClient.cs`:

Add the enum near the top (after `SignalingReconnectPolicy`):

```csharp
/// <summary>Why a signaling connection ended, for diagnostics — never derived from free text.</summary>
public enum SignalingCloseReason { NormalClosure, SessionEnded, ReconnectExhausted, SessionGone }
```

Add the two event declarations next to `StateChanged`:

```csharp
public event Action<int>? ReconnectAttempting;
public event Action<SignalingCloseReason>? Closed;
```

In `CloseAsync`, right before `SetState(SignalingConnectionState.Closed);` at the end, add:

```csharp
Closed?.Invoke(SignalingCloseReason.NormalClosure);
```

In `CloseFromReceiveLoopAsync`, right before its final `SetState(SignalingConnectionState.Closed);`, add:

```csharp
Closed?.Invoke(SignalingCloseReason.SessionEnded);
```

In `HandleSessionGoneAsync`, right before its `SetState(SignalingConnectionState.Closed);`, add:

```csharp
Closed?.Invoke(SignalingCloseReason.SessionGone);
```

In `TryReconnectAsync`, fire the attempt event right after entering the loop body, before the delay:

```csharp
for (var attempt = 0; reconnectPolicy.MaxAttempts is null || attempt < reconnectPolicy.MaxAttempts; attempt++)
{
    ReconnectAttempting?.Invoke(attempt);
    try
    {
        await reconnectDelay.DelayAsync(ReconnectDelayFor(attempt), cancellationToken);
        await OpenConnectionAsync(cancellationToken);
        return ReconnectOutcome.Reconnected;
    }
    ...
```

In `RunReceiveLoopAsync`, in the `default:` branch of the `switch (await TryReconnectAsync(cancellationToken))` (the exhausted/faulted path), add the close event before `SetState(SignalingConnectionState.Faulted); return;`:

```csharp
default:
    Closed?.Invoke(SignalingCloseReason.ReconnectExhausted);
    SetState(SignalingConnectionState.Faulted);
    return;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Signaling.Tests`
Expected: PASS — all new tests plus every pre-existing `SignalingClientTests` case (the new events are additive; no existing behavior changed).

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Windows.Signaling/SignalingClient.cs src/SonicRelay.Windows.Signaling/ISignalingClient.cs tests/SonicRelay.Windows.Signaling.Tests/SignalingClientTests.cs
git commit -m "Add SignalingClient close-reason and reconnect-attempt events"
```

---

### Task 3: `WebRtcPublisher` ICE-restart event

**Files:**
- Modify: `src/SonicRelay.Windows.WebRtc/WebRtcPublisher.cs`
- Modify: `src/SonicRelay.Windows.WebRtc/IWebRtcPublisher.cs` (add the event to the interface)
- Test: `tests/SonicRelay.Windows.WebRtc.Tests/WebRtcPublisherTests.cs`

**Interfaces:**
- Produces: `IWebRtcPublisher.IceRestartRequested: event Action<string>?` (viewer id). Consumed by `PublisherRuntime` in Task 4.

- [ ] **Step 1: Write the failing test**

Append to `tests/SonicRelay.Windows.WebRtc.Tests/WebRtcPublisherTests.cs` (after `ParticipantReconnectedRestartsIceOnTheExistingPeerInsteadOfRecreatingIt`):

```csharp
    [Fact]
    public async Task ParticipantReconnectedRaisesIceRestartRequestedForTheViewer()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        await ReadyAsync(publisher, "viewer-1");
        var requested = new List<string>();
        publisher.IceRestartRequested += requested.Add;

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        Assert.Equal(["viewer-1"], requested);
    }

    [Fact]
    public async Task ParticipantReconnectedForAnUnregisteredViewerDoesNotRaiseIceRestartRequested()
    {
        var context = CreateContext();
        await using var publisher = context.Publisher;
        var requested = new List<string>();
        publisher.IceRestartRequested += requested.Add;

        await publisher.HandleAsync(new(SignalingMessageTypes.ParticipantReconnected, "session-1", From: "viewer-1"));

        Assert.Empty(requested);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SonicRelay.Windows.WebRtc.Tests --filter IceRestartRequested`
Expected: FAIL to compile — `WebRtcPublisher`/`IWebRtcPublisher` has no `IceRestartRequested`.

- [ ] **Step 3: Add the event and fire it**

In `src/SonicRelay.Windows.WebRtc/IWebRtcPublisher.cs`, add next to `DiagnosticsChanged`:

```csharp
event Action<string>? IceRestartRequested;
```

In `src/SonicRelay.Windows.WebRtc/WebRtcPublisher.cs`, add the event declaration next to `DiagnosticsChanged`:

```csharp
public event Action<string>? IceRestartRequested;
```

In `ReofferToViewerAsync`, fire it only on the actual restart path (not the fallback-to-fresh-offer path):

```csharp
private async Task ReofferToViewerAsync(string sessionId, string viewerId, CancellationToken cancellationToken)
{
    try
    {
        var restartOffer = await peers.RequestIceRestartAsync(viewerId, cancellationToken);
        if (restartOffer is null)
        {
            await OfferToViewerAsync(sessionId, viewerId, cancellationToken);
            return;
        }
        IceRestartRequested?.Invoke(viewerId);
        await SendOfferAsync(sessionId, viewerId, restartOffer, cancellationToken);
    }
    catch
    {
        await peers.RemoveViewerAsync(viewerId, CancellationToken.None);
        throw;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.WebRtc.Tests`
Expected: PASS — the two new tests plus every pre-existing `WebRtcPublisherTests` case.

- [ ] **Step 5: Commit**

```bash
git add src/SonicRelay.Windows.WebRtc/WebRtcPublisher.cs src/SonicRelay.Windows.WebRtc/IWebRtcPublisher.cs tests/SonicRelay.Windows.WebRtc.Tests/WebRtcPublisherTests.cs
git commit -m "Add WebRtcPublisher.IceRestartRequested event"
```

---

### Task 4: Wire the new events into `PublisherRuntime` diagnostics

**Files:**
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`

**Interfaces:**
- Consumes: `ISignalingClient.ReconnectAttempting`/`Closed` (Task 2), `IWebRtcPublisher.IceRestartRequested` (Task 3), `DiagnosticLog.WriteAsync` (existing).
- Produces: no new public surface — this task only adds subscriptions and diagnostic writes. `PublisherRuntime` has no dedicated test file today (it is untested composition/glue, consistent with the rest of the class); this task is verified by the manual pass in Task 6 and by the fact that Tasks 2–3's events are independently tested.

`PublisherRuntime` is constructed from a private constructor invoked only by the static `Create` factory, which already holds local variables `signaling` and `webRtcPublisher` before building the `PublisherRuntime` instance. Subscribe there, right after the existing lines that register `signalingHandlers.Register(webRtcPublisher);` (see the existing `Create` method body):

- [ ] **Step 1: Subscribe to the new events in `Create`**

In `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs`, inside `Create`, immediately after `signalingHandlers.Register(webRtcPublisher);` and before `var audio = audioCapture;`, add:

```csharp
signaling.ReconnectAttempting += attempt => LogReconnectAttempt(diagnosticLog, attempt);
signaling.Closed += reason => LogSignalingClosed(diagnosticLog, reason);
webRtcPublisher.IceRestartRequested += viewerId => LogIceRestart(diagnosticLog, viewerId);
```

This requires the `DiagnosticLog` instance to exist before `PublisherRuntime`'s constructor runs (today it's created inside the constructor). Move its creation earlier in `Create`, right before the `signalingHandlers` block:

```csharp
var diagnosticLog = new DiagnosticLog();
var signalingHandlers = new CompositeSignalingMessageHandler();
var signaling = new SignalingClient(configuration, tokenStore, [signalingHandlers]);
```

And change the constructor to accept and store the already-created `diagnosticLog` instead of `new`-ing its own:

```csharp
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
    DiagnosticLog diagnosticLog)
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
    ReportExporter = new DiagnosticReportExporter();
    Workflow.StateChanged += OnWorkflowStateChanged;
    _ = WriteDiagnosticAsync("runtime", "Publisher runtime configured.", new Dictionary<string, string>
    {
        ["backend"] = DiagnosticRedactor.BackendHost(backendBaseUrl)
    });
}
```

And update the final `return` in `Create` to pass `diagnosticLog` through:

```csharp
return new PublisherRuntime(http, workflow, normalized, peers, webRtcPublisher, audioBridge, relayPreference, audioQuality, audio, audioOutput, diagnosticLog);
```

- [ ] **Step 2: Add the three logging helpers**

Add these private static/instance helpers to `PublisherRuntime` (near `WriteDiagnosticAsync`); they are `static` where they don't need `this` so they can run from the lambdas registered during `Create`, before the instance exists:

```csharp
private static readonly IReadOnlyDictionary<string, string> NoProperties = new Dictionary<string, string>();

private static async void LogReconnectAttempt(DiagnosticLog log, int attempt)
{
    try { await log.WriteAsync("reconnect-attempt", $"Signaling reconnect attempt {attempt + 1}.", NoProperties); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
}

private static async void LogSignalingClosed(DiagnosticLog log, SignalingCloseReason reason)
{
    try
    {
        await log.WriteAsync("signaling-closed", "Signaling connection closed.", new Dictionary<string, string>
        {
            ["reason"] = reason.ToString()
        });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
}

private static async void LogIceRestart(DiagnosticLog log, string viewerId)
{
    try
    {
        await log.WriteAsync("ice-restart", "ICE restart requested for a reconnected viewer.", new Dictionary<string, string>
        {
            ["viewerId"] = viewerId
        });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
}
```

Note: `viewerId` is a server-assigned participant id, not PII, so it is passed as-is (matching how `ParticipantId`/`SessionId` already appear unredacted in the API's own log lines); no redaction gap is introduced.

- [ ] **Step 3: Build and run the full Presentation + Signaling + WebRtc test suites**

Run: `dotnet test tests/SonicRelay.Windows.Presentation.Tests tests/SonicRelay.Windows.Signaling.Tests tests/SonicRelay.Windows.WebRtc.Tests`
Expected: PASS — this task adds no new tests of its own (see Interfaces note above) but must not break any existing ones; a build failure or any red test means the wiring is wrong.

- [ ] **Step 4: Commit**

```bash
git add src/SonicRelay.Windows.Presentation/PublisherRuntime.cs
git commit -m "Wire signaling/ICE-restart diagnostic events into PublisherRuntime"
```

---

### Task 5: Tray/window-state diagnostic events

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/DesktopTrayController.cs`

**Interfaces:**
- Produces: `MainWindowViewModel.LogDiagnostic(string category, string message): void` — a no-op when no runtime is attached, otherwise forwards to `runtime.DiagnosticLog.WriteAsync`. Consumed by `DesktopTrayController`.

`DesktopTrayController` only holds a `MainWindowViewModel`, not the `PublisherRuntime` (which is private to `MainWindowViewModel`). Add a small forwarding method rather than exposing the runtime itself, keeping `PublisherRuntime` encapsulated.

- [ ] **Step 1: Add `LogDiagnostic` to `MainWindowViewModel`**

In `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`, add this method (near `Run`):

```csharp
/// <summary>
/// Forwards a diagnostic event to the attached runtime's log, or does nothing if no
/// runtime is attached (the standalone preview launch). Diagnostics must never throw
/// into the caller, matching PublisherRuntime.WriteDiagnosticAsync's own guarantee.
/// </summary>
public void LogDiagnostic(string category, string message)
{
    if (runtime is null) return;
    _ = LogAsync(runtime.DiagnosticLog, category, message);

    static async Task LogAsync(SonicRelay.Windows.Core.Diagnostics.DiagnosticLog log, string category, string message)
    {
        try { await log.WriteAsync(category, message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}
```

- [ ] **Step 2: Call it from the tray/window handlers**

In `src/SonicRelay.Windows.Desktop/DesktopTrayController.cs`, update `OnWindowClosing` and `OnWindowPropertyChanged`:

```csharp
private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
{
    // A deliberate Quit (menu or shutdown) closes for real; otherwise honour the tray policy.
    if (quitting) return;
    if (controller.DecideOnClose(viewModel.CurrentSnapshot) == TrayCloseDecision.Hide)
    {
        e.Cancel = true;
        window.Hide();
        viewModel.LogDiagnostic("window-state", "Window close intercepted; kept running in tray.");
    }
    else
    {
        viewModel.LogDiagnostic("window-state", "Window closing for real (no active session to keep alive).");
    }
}

private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
{
    if (e.Property != Window.WindowStateProperty || (WindowState)e.NewValue! != WindowState.Minimized)
        return;
    if (controller.DecideOnMinimize() == TrayCloseDecision.Hide)
    {
        // Restore the stored state so the next Show opens normally rather than minimised.
        window.WindowState = WindowState.Normal;
        window.Hide();
        viewModel.LogDiagnostic("window-state", "Minimized to tray.");
    }
}
```

- [ ] **Step 3: Build the Desktop project**

Run: `dotnet build src/SonicRelay.Windows.Desktop`
Expected: builds cleanly (this task has no dedicated automated test — `DesktopTrayController` has no existing test file and Avalonia `Window`/`WindowClosingEventArgs` are not easily fakeable outside a real windowing context; this is verified manually in Task 6's manual pass instead).

- [ ] **Step 4: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs src/SonicRelay.Windows.Desktop/DesktopTrayController.cs
git commit -m "Log window-state diagnostics on minimize-to-tray and close"
```

---

### Task 6: Export/Clear commands and Diagnostics page wiring

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`
- Modify: `src/SonicRelay.Windows.Desktop/Controls/DiagnosticsView.axaml`
- Test: `tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelDiagnosticsTests.cs` (new file)

**Interfaces:**
- Produces: `MainWindowViewModel.ExportDiagnosticsCommand: RelayCommand`, `MainWindowViewModel.ClearDiagnosticsCommand: RelayCommand`, `MainWindowViewModel.ClearLogsArmed: bool`, `MainWindowViewModel.DiagnosticsActionMessage: string?`.

Since `PublisherRuntime` has no public interface and is not fakeable in tests (confirmed: no existing test attaches a real or fake runtime to `MainWindowViewModel`), this task tests the command logic through a small extraction that *is* independently testable — `DiagnosticsActions`, a static helper in the same file as the view model logic, taking a `DiagnosticLog` directly. The `RelayCommand` on `MainWindowViewModel` is a thin wrapper around it (consistent with how `Run(Func<PublisherWorkflow, Task>)` already wraps workflow calls without runtime being independently testable).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelDiagnosticsTests.cs
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Tests;

public sealed class MainWindowViewModelDiagnosticsTests
{
    [Fact]
    public async Task ExportProducesASanitizedSuccessMessageWithTheExportedPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            await log.WriteAsync("auth", "signed in");

            var message = await DiagnosticsActions.ExportAsync(log);

            Assert.Contains("Exported", message, StringComparison.Ordinal);
            Assert.Contains(directory, message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExportFailureReturnsASanitizedMessageNeverARawException()
    {
        // Force a real failure: a file sitting where the log directory needs to be makes
        // Directory.CreateDirectory (inside ExportAsync) throw IOException.
        var blockingFilePath = Path.Combine(Path.GetTempPath(), $"sonicrelay-blocked-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFilePath, "not a directory");
        try
        {
            var log = new DiagnosticLog(blockingFilePath);

            var message = await DiagnosticsActions.ExportAsync(log);

            Assert.Contains("failed", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    [Fact]
    public async Task ClearReturnsASanitizedSuccessMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-{Guid.NewGuid():N}");
        try
        {
            var log = new DiagnosticLog(directory);
            await log.WriteAsync("auth", "signed in");

            var message = await DiagnosticsActions.ClearAsync(log);

            Assert.Contains("Cleared", message, StringComparison.Ordinal);
            Assert.Empty(log.RecentEvents);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ClearLogsArmedTogglesOnFirstClickAndResetsAfterASecondCommandRuns()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.ClearLogsArmed);
        vm.ArmClearLogs();
        Assert.True(vm.ClearLogsArmed);
        vm.DisarmClearLogs();
        Assert.False(vm.ClearLogsArmed);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter "MainWindowViewModelDiagnosticsTests"`
Expected: FAIL to compile — `DiagnosticsActions`, `ClearLogsArmed`, `ArmClearLogs`, `DisarmClearLogs` don't exist yet.

- [ ] **Step 3: Add `DiagnosticsActions` and wire the commands**

Create `src/SonicRelay.Windows.Desktop/ViewModels/DiagnosticsActions.cs`:

```csharp
using SonicRelay.Windows.Core.Diagnostics;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// The testable core of the Diagnostics page's Export/Clear actions: sanitized
/// success/failure messages, never a raw exception. Kept independent of
/// MainWindowViewModel/PublisherRuntime (neither is fakeable in tests today).
/// </summary>
public static class DiagnosticsActions
{
    public static async Task<string> ExportAsync(DiagnosticLog log)
    {
        try
        {
            var path = await log.ExportAsync();
            return $"Exported diagnostics to {path}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Export failed: could not write the log file.";
        }
    }

    public static async Task<string> ClearAsync(DiagnosticLog log)
    {
        try
        {
            await log.ClearAsync();
            return "Cleared the diagnostic log.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Clear failed: could not delete the log file(s).";
        }
    }
}
```

In `src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs`:

Add fields/properties near the other bindable state:

```csharp
private bool clearLogsArmed;
private string? diagnosticsActionMessage;

public bool ClearLogsArmed
{
    get => clearLogsArmed;
    private set => SetProperty(ref clearLogsArmed, value);
}

public string? DiagnosticsActionMessage
{
    get => diagnosticsActionMessage;
    private set
    {
        if (SetProperty(ref diagnosticsActionMessage, value))
            RaisePropertyChanged(nameof(HasDiagnosticsActionMessage));
    }
}

/// <summary>Bindable presence check, following the same pattern as <see cref="HasSessionCode"/>
/// rather than an Avalonia converter (there is no existing null/bool-to-visibility converter
/// wired up anywhere in this codebase to reuse).</summary>
public bool HasDiagnosticsActionMessage => diagnosticsActionMessage is not null;

public void ArmClearLogs() => ClearLogsArmed = true;
public void DisarmClearLogs() => ClearLogsArmed = false;
```

Add the two commands in the constructor, next to the other `RelayCommand` assignments:

```csharp
ExportDiagnosticsCommand = new RelayCommand(ExportDiagnosticsAsync, () => runtime is not null);
ClearDiagnosticsCommand = new RelayCommand(ClearDiagnosticsAsync, () => runtime is not null);
```

Add the command properties next to the other `RelayCommand` properties:

```csharp
public RelayCommand ExportDiagnosticsCommand { get; }
public RelayCommand ClearDiagnosticsCommand { get; }
```

Add the two backing methods (near `Run`):

```csharp
private async Task ExportDiagnosticsAsync()
{
    DisarmClearLogs();
    if (runtime is null) return;
    DiagnosticsActionMessage = await DiagnosticsActions.ExportAsync(runtime.DiagnosticLog);
}

private async Task ClearDiagnosticsAsync()
{
    if (runtime is null) return;
    if (!ClearLogsArmed)
    {
        ArmClearLogs();
        return;
    }
    DisarmClearLogs();
    DiagnosticsActionMessage = await DiagnosticsActions.ClearAsync(runtime.DiagnosticLog);
}
```

Register the two new commands in `RaiseCommandStates`:

```csharp
private void RaiseCommandStates()
{
    CreateSessionCommand.RaiseCanExecuteChanged();
    StartAudioCommand.RaiseCanExecuteChanged();
    StopAudioCommand.RaiseCanExecuteChanged();
    EndSessionCommand.RaiseCanExecuteChanged();
    RetryCommand.RaiseCanExecuteChanged();
    LogoutCommand.RaiseCanExecuteChanged();
    ExportDiagnosticsCommand.RaiseCanExecuteChanged();
    ClearDiagnosticsCommand.RaiseCanExecuteChanged();
}
```

- [ ] **Step 4: Rewire `DiagnosticsView.axaml`**

Add a tiny converter for the arm/confirm button label (Avalonia has no built-in bool-to-string
converter, and nothing in this codebase has one to reuse). Create
`src/SonicRelay.Windows.Desktop/Controls/ClearLogsLabelConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;

namespace SonicRelay.Windows.Desktop.Controls;

public sealed class ClearLogsLabelConverter : IValueConverter
{
    public static readonly ClearLogsLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Confirm clear?" : "Clear logs";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

The action-feedback line uses the new `HasDiagnosticsActionMessage` bool property (Step 3) for
`IsVisible`, the same pattern `HasSessionCode` already uses elsewhere in this view model —
no converter needed for that part.

Replace `src/SonicRelay.Windows.Desktop/Controls/DiagnosticsView.axaml` in full:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="using:SonicRelay.Windows.Desktop.Controls"
             xmlns:vm="using:SonicRelay.Windows.Desktop.ViewModels"
             x:Class="SonicRelay.Windows.Desktop.Controls.DiagnosticsView"
             x:DataType="vm:MainWindowViewModel">
  <DockPanel LastChildFill="True">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="24,24,24,0" HorizontalAlignment="Right">
      <Button Content="Export logs" Command="{Binding ExportDiagnosticsCommand}" />
      <Button Command="{Binding ClearDiagnosticsCommand}">
        <TextBlock Text="{Binding ClearLogsArmed, Converter={x:Static controls:ClearLogsLabelConverter.Instance}}" />
      </Button>
    </StackPanel>
    <TextBlock DockPanel.Dock="Top" Margin="24,4,24,0" Classes="mono"
               IsVisible="{Binding HasDiagnosticsActionMessage}"
               Text="{Binding DiagnosticsActionMessage}" />
    <controls:TechnicalConsole Margin="24" DataContext="{Binding Shell}" />
  </DockPanel>
</UserControl>
```

In `src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml`, remove the `DataContext="{Binding Shell}"` override on line 104 so `DiagnosticsView` inherits `MainWindowViewModel` from its parent:

```xml
<controls:DiagnosticsView
    IsVisible="{Binding ((vm:MainWindowViewModel)DataContext).IsDiagnostics, ElementName=RootWindow}" />
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SonicRelay.Windows.Desktop.Tests --filter "MainWindowViewModelDiagnosticsTests"`
Expected: PASS.

Run: `dotnet build src/SonicRelay.Windows.Desktop`
Expected: builds cleanly (validates the XAML/converter wiring).

- [ ] **Step 6: Manual verification**

Run the app (`dotnet run --project src/SonicRelay.Windows.Desktop`), open the Diagnostics page, click **Export logs** (confirm a file appears at `%LocalAppData%\SonicRelay\WindowsPublisher\diagnostics\sonicrelay-logs-*.jsonl`), click **Clear logs** once (button changes to "Confirm clear?"), click again (confirm the log file(s) are gone and the message updates), minimize the window to the tray and restore it, and confirm `window-state` events show up in the exported log afterward.

- [ ] **Step 7: Commit**

```bash
git add src/SonicRelay.Windows.Desktop/ViewModels/MainWindowViewModel.cs src/SonicRelay.Windows.Desktop/ViewModels/DiagnosticsActions.cs src/SonicRelay.Windows.Desktop/Controls/DiagnosticsView.axaml src/SonicRelay.Windows.Desktop/Controls/ClearLogsLabelConverter.cs src/SonicRelay.Windows.Desktop/Views/MainWindow.axaml tests/SonicRelay.Windows.Desktop.Tests/MainWindowViewModelDiagnosticsTests.cs
git commit -m "Add Export/Clear diagnostics commands and wire the Diagnostics page"
```
