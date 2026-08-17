using SonicRelay.Windows.Core.Diagnostics;
using Xunit;

namespace SonicRelay.Windows.Core.Tests;

public sealed class RecoveryJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-journal-{Guid.NewGuid():N}");

    [Fact]
    public async Task Records_the_generation_and_attempt_that_produced_the_event()
    {
        using var log = new DiagnosticLog(directory);
        var journal = new DiagnosticRecoveryJournal(log);

        await journal.RecordAsync(RecoveryEvents.NetworkLost, generation: 7, attempt: 3);

        var entry = Assert.Single(log.RecentEvents);
        Assert.Equal("Recovery", entry.Category);
        Assert.Equal(RecoveryEvents.NetworkLost, entry.Message);
        Assert.Equal("7", entry.Properties["generation"]);
        Assert.Equal("3", entry.Properties["attempt"]);
    }

    [Fact]
    public async Task Carries_the_state_transition_and_reason_of_a_recovery_step()
    {
        using var log = new DiagnosticLog(directory);
        var journal = new DiagnosticRecoveryJournal(log);

        await journal.RecordAsync(RecoveryEvents.SignalingReconnectStarted, generation: 1, attempt: 0,
            new Dictionary<string, string>
            {
                ["previousState"] = "Connected",
                ["newState"] = "Reconnecting",
                ["reason"] = "socket closed",
                ["elapsedMs"] = "1200",
            });

        var entry = Assert.Single(log.RecentEvents);
        Assert.Equal("Connected", entry.Properties["previousState"]);
        Assert.Equal("Reconnecting", entry.Properties["newState"]);
        Assert.Equal("socket closed", entry.Properties["reason"]);
        Assert.Equal("1200", entry.Properties["elapsedMs"]);
    }

    [Fact]
    public async Task Redacts_a_secret_that_leaks_into_a_recovery_property()
    {
        using var log = new DiagnosticLog(directory);
        var journal = new DiagnosticRecoveryJournal(log);

        await journal.RecordAsync(RecoveryEvents.RecoveryFailed, generation: 2, attempt: 1,
            new Dictionary<string, string> { ["reason"] = "rejected: Bearer eyJhbGciOi.payload.signature" });

        // The journal exists to be readable by whoever is debugging an outage, which is exactly
        // the situation where a raw error string carrying a token is most likely to be pasted
        // into a bug report. Routing through DiagnosticLog is what keeps that from happening.
        Assert.DoesNotContain("eyJhbGciOi", Assert.Single(log.RecentEvents).Properties["reason"],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_journal_accepts_every_event_without_writing_one()
    {
        await NullRecoveryJournal.Instance.RecordAsync(RecoveryEvents.MediaResumed, generation: 1, attempt: 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
