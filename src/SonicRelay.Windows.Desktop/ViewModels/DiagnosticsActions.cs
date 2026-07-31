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
