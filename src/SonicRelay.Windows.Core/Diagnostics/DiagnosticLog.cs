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
