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
