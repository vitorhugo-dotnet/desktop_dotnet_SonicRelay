using Microsoft.UI.Xaml;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.App;

public partial class App : Application
{
    private Window? _window;
    private PublisherRuntime? runtime;

    public static App CurrentApp => (App)Current;
    public PublisherRuntime? Runtime => runtime;
    public event Action<PublisherRuntime?>? RuntimeChanged;

    /// <summary>
    /// App-global background/tray preferences (issue #26). These are independent of
    /// the configured backend, so they live on the app rather than the runtime.
    /// </summary>
    public TrayBackgroundPreferenceStore TrayPreferences { get; } = new();

    /// <summary>
    /// App-global appearance preferences (issue #30): theme, backdrop material and
    /// acrylic opacity. Applied by <see cref="MainWindow"/> at startup and whenever
    /// changed from the Settings page.
    /// </summary>
    public AppearancePreferenceStore AppearancePreferences { get; } = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        // "Start minimized to tray": keep the process running but never surface the
        // window on launch. Only honoured when the app is allowed to live in the tray.
        if (TrayPreferences.StartMinimized && TrayPreferences.KeepRunningInTray && _window is MainWindow main)
        {
            main.HideToTrayOnStartup();
        }

        _ = LoadConfiguredRuntimeAsync();
    }

    /// <summary>Re-applies appearance preferences to the main window (called from Settings).</summary>
    public void ApplyAppearance() => (_window as MainWindow)?.ApplyAppearance();

    /// <summary>
    /// Bootstraps the publisher device against <paramref name="backendBaseUrl"/>,
    /// swaps in the initialized runtime, and persists the URL for the next launch.
    /// </summary>
    public async Task ConfigureBackendAsync(Uri backendBaseUrl)
    {
        // The WASAPI-backed capture service is the Windows platform adapter; the
        // shared runtime composition only sees IAudioCaptureService (issue #32).
        var replacement = PublisherRuntime.Create(backendBaseUrl, new AudioCaptureService());
        await replacement.InitializeDeviceIdentityAsync();
        var previous = runtime;
        runtime = replacement;
        RuntimeChanged?.Invoke(runtime);
        if (previous is not null) await previous.DisposeAsync();
        try
        {
            await new UserConfigurationLoader().SaveBackendAsync(backendBaseUrl);
        }
        catch
        {
            // Persisting the backend is best-effort; the session still works.
        }
    }

    public async Task DisposeRuntimeAsync()
    {
        var previous = runtime;
        runtime = null;
        RuntimeChanged?.Invoke(null);
        if (previous is not null) await previous.DisposeAsync();
    }

    private async Task LoadConfiguredRuntimeAsync()
    {
        try
        {
            var configuration = await new UserConfigurationLoader().LoadAsync();
            await ConfigureBackendAsync(configuration.BackendBaseUrl);
        }
        catch
        {
            RuntimeChanged?.Invoke(null);
        }
    }
}
