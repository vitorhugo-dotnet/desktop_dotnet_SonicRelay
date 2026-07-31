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
