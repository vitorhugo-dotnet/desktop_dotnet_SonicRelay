# Connection-Loss Diagnostics: Export, Clear, Retention, and Richer Events

## Goal

The publisher still loses connection when the window is minimized, the app is closed, or
even while it stays open in the foreground, and today's diagnostics can't say why. Extend
`DiagnosticLog` so a user can capture a support-ready log covering exactly what happened
around a drop, export it as one file, clear it, and never accumulate an unbounded pile of
`.jsonl` files on disk. `DiagnosticReportExporter` is a separate, pre-existing feature (a
point-in-time Markdown status snapshot) that is also not wired to any button today; this
work does not touch it — the user asked for the actual log history, which lives in
`DiagnosticLog`'s `.jsonl` files, not a status snapshot.

## Scope

In scope: retention/cleanup of on-disk logs, a "Clear logs" action, and new diagnostic
events at the transitions most likely to correlate with a loss (minimize/restore to tray,
window close, reconnect attempts, ICE restarts, and the concrete reason a signaling
WebSocket closed). Out of scope: fixing the underlying reconnection behavior itself (that
already exists — jitmath backoff, ICE restart on `participant.reconnected`) and any
server-side change (tracked separately in `dotnet_SonicRelay`).

## Architecture

`DiagnosticLog` gains two additions, both in `SonicRelay.Windows.Core.Diagnostics`:

- **Retention**: on construction, `DiagnosticLog` deletes `publisher-*.jsonl` files in its
  log directory older than a configurable threshold (default 3 days). This runs once per
  process start, best-effort (I/O errors are swallowed the same way `WriteAsync` already
  swallows them — diagnostics must never interrupt publisher operation).
- **Clear**: a new `ClearAsync()` method deletes every `publisher-*.jsonl` file under the
  log directory and empties `RecentEvents`. `WriteAsync` currently updates `RecentEvents`
  *before* acquiring `writeLock` and only appends to disk once it has the lock; holding
  `writeLock` in `ClearAsync` alone would not stop a write already past that in-memory
  update from landing on disk right after the clear. `WriteAsync` needs to move its
  `RecentEvents` update inside the same `writeLock` critical section as the disk append,
  so both mutations serialize with `ClearAsync` as one unit — only then does holding
  `writeLock` in `ClearAsync` guarantee no write can reappear after a clear.
- **Export**: a new `ExportAsync()` method concatenates every retained `publisher-*.jsonl`
  file (oldest first) into a single file,
  `<diagnostics-directory>/sonicrelay-logs-{yyyyMMdd-HHmmss}.jsonl`, and returns its path.
  Every line is already redacted at write time, so a byte-level concatenation needs no
  further sanitization.

New call sites write additional `DiagnosticEvent` categories through the existing
`WriteDiagnosticAsync` wrapper in `PublisherRuntime`:

- `window-state`: minimized to tray / restored from tray / close-to-tray vs. real exit
  (wired from the tray/shell code added in the close-to-tray work).
- `signaling-closed`: fired when the signaling client's connection ends, carrying a
  bounded `reason` property (`normal`, `timeout`, `transport-error`, `server-closed`,
  `cancelled`) rather than a raw exception message. `SignalingClient` needs a small change
  to surface *why* the socket closed instead of just that it closed, mirroring the
  category split the API side will add for the same purpose.
- `reconnect-attempt` / `ice-restart`: existing reconnect-with-jitter and ICE-restart code
  paths gain a diagnostic write at the point they already fire, using data already
  available there (no new state needed).

## UI

`DiagnosticReportExporter` exists but nothing in the UI calls it yet — there is no Export
button today, only the read-only `TechnicalConsole` activity log on the Diagnostics page.
This work adds both **Export logs** and **Clear logs** as `RelayCommand`s on
`MainWindowViewModel` (the same pattern as `CreateSessionCommand`, `RetryCommand`, etc.),
surfaced through a new `DiagnosticsActionMessage` string property for sanitized
success/failure feedback (never a raw exception). `DiagnosticsView.axaml` stops rebinding
its own `DataContext` to `Shell` and instead inherits `MainWindowViewModel` from its
parent (`TechnicalConsole` keeps its existing `DashboardShellViewModel` contract by
getting `DataContext="{Binding Shell}"` set explicitly where it's placed inside
`DiagnosticsView`), so the new buttons can bind directly to the view model that owns the
runtime.

The codebase has no modal/confirmation-dialog infrastructure anywhere (checked: no
`ContentDialog`, `MessageBox`, or dialog service), and every existing destructive-ish
action (`EndSessionCommand`, `LogoutCommand`) executes immediately with no confirmation.
Clear follows that same convention but arms itself first: the first click flips the
button to an armed "Confirm clear?" state (a bindable `ClearLogsArmed` bool) without
deleting anything; a second click while armed performs the delete. Any other command
execution or navigating away disarms it. This avoids introducing new dialog
infrastructure while still requiring a deliberate second action for a non-undoable
operation.

## Data and safety

Retention and clear reuse the existing redaction and user-scoped storage guarantees — no
new surface for credentials or PII. The `reason` properties added to new events are drawn
from a fixed enum, never from free-text exception messages, so they can't leak connection
strings or stack traces into the log file.

## Verification

Core tests cover: cleanup deletes files older than the threshold and keeps newer ones,
`ClearAsync` empties both the file(s) and `RecentEvents`, and the new event categories
serialize through the existing redactor without exposing raw properties. A manual pass
minimizes/restores the app and force-closes the signaling socket to confirm the new events
appear with a correct `reason`.
