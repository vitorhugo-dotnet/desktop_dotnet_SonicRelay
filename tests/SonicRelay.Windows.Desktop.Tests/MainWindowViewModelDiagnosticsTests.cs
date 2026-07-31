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
