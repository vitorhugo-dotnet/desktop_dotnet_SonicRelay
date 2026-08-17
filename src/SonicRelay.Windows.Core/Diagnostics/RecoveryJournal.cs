using System.Globalization;

namespace SonicRelay.Windows.Core.Diagnostics;

/// <summary>
/// The fixed vocabulary of recovery steps, shared verbatim with the Flutter viewer's
/// <c>RecoveryEvents</c>. Recovery spans two codebases and a backend, so a failed reconnect is
/// only diagnosable if both ends name the same step the same way — otherwise correlating a
/// publisher's log against a viewer's means translating between two ad-hoc phrasings.
/// </summary>
public static class RecoveryEvents
{
    public const string NetworkLost = "network_lost";
    public const string NetworkRestored = "network_restored";
    public const string RecoveryStarted = "recovery_started";
    public const string RecoveryCancelled = "recovery_cancelled";
    public const string StaleAttemptIgnored = "stale_attempt_ignored";
    public const string SignalingReconnectStarted = "signaling_reconnect_started";
    public const string SignalingReconnectSucceeded = "signaling_reconnect_succeeded";
    public const string SessionRejoinStarted = "session_rejoin_started";
    public const string SessionRejoinSucceeded = "session_rejoin_succeeded";
    public const string IceRestartStarted = "ice_restart_started";
    public const string IceRestartSucceeded = "ice_restart_succeeded";
    public const string PeerRebuildStarted = "peer_rebuild_started";
    public const string PeerRebuildSucceeded = "peer_rebuild_succeeded";
    public const string MediaResumed = "media_resumed";
    public const string RecoveryFailed = "recovery_failed";
}

/// <summary>
/// Records one line per recovery step, tagged with the connection generation and attempt that
/// produced it.
/// </summary>
/// <remarks>
/// The generation is what makes an out-of-order log readable. A recovery attempt that completes
/// after a newer one has already taken over still logs, and without a generation stamp its lines
/// are indistinguishable from the live attempt's — which is precisely the confusion that makes
/// "the session came back connected but with no media" so hard to pin down after the fact.
/// </remarks>
public interface IRecoveryJournal
{
    Task RecordAsync(
        string @event,
        int generation,
        int attempt,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IRecoveryJournal"/> over the publisher's <see cref="DiagnosticLog"/>, so recovery
/// lines land in the same redacted, retained, exportable file as everything else. Redaction is
/// the log's job and is deliberately not re-implemented here: a recovery reason is usually a raw
/// error string, which is exactly where a token or an SDP body leaks in.
/// </summary>
public sealed class DiagnosticRecoveryJournal(DiagnosticLog log) : IRecoveryJournal
{
    private const string Category = "Recovery";

    private readonly DiagnosticLog log = log ?? throw new ArgumentNullException(nameof(log));

    public Task RecordAsync(
        string @event,
        int generation,
        int attempt,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@event);
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generation"] = generation.ToString(CultureInfo.InvariantCulture),
            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var pair in properties ?? new Dictionary<string, string>())
        {
            payload[pair.Key] = pair.Value;
        }
        return log.WriteAsync(Category, @event, payload, cancellationToken);
    }
}

/// <summary>No-op journal for callers with no diagnostic log wired up (tests, headless tools).</summary>
public sealed class NullRecoveryJournal : IRecoveryJournal
{
    public static NullRecoveryJournal Instance { get; } = new();

    private NullRecoveryJournal()
    {
    }

    public Task RecordAsync(
        string @event,
        int generation,
        int attempt,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
