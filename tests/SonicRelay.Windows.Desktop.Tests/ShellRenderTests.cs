using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.Controls;
using SonicRelay.Windows.Desktop.Converters;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Desktop.Views;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Headless UI smoke tests for the shell (issue #32): the window must lay out and rasterize
/// the full design system without binding or resource errors, and the status-brush mapping
/// must resolve real token brushes. When SHELL_SHOT_DIR is set, a PNG of the shell is written
/// there for visual review against the Lovable prototype.
/// </summary>
public sealed class ShellRenderTests
{
    [AvaloniaFact]
    public void Shell_renders_streaming_preview_to_a_frame()
    {
        var window = new MainWindow
        {
            DataContext = MainWindowViewModel.CreatePreview(),
        };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 800, $"unexpected width {frame.PixelSize.Width}");
        Assert.True(frame.PixelSize.Height > 500, $"unexpected height {frame.PixelSize.Height}");

        var dir = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, "shell-preview.png"));
        }
    }

    [AvaloniaFact]
    public void Diagnostics_page_renders_when_selected()
    {
        var viewModel = MainWindowViewModel.CreatePreview();
        viewModel.SelectedNavigation = viewModel.Navigation.Single(item => item.Key == PageKey.Diagnostics);
        var window = new MainWindow { DataContext = viewModel };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(viewModel.IsDiagnostics);

        var dir = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "diagnostics-preview.png"));
        }
    }

    [AvaloniaFact]
    public void The_session_page_is_gone_from_navigation()
    {
        // The Session page duplicated the dashboard's cards and top-bar status rows, so it was
        // removed; the sidebar must not offer it any more.
        var viewModel = MainWindowViewModel.CreatePreview();
        Assert.DoesNotContain(viewModel.Navigation, item => item.Label == "Session");
    }

    [AvaloniaFact]
    public void Pairing_surface_renders_when_the_pairing_page_is_selected()
    {
        // Pairing stays reachable even without a device identity (Task 3's gate locks every
        // other destination, not this one) — a fresh, unauthenticated view model already
        // defaults here, and this selects it explicitly to render the pairing surface.
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedNavigation = viewModel.Navigation.Single(item => item.Key == PageKey.Pairing);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(viewModel.IsPairing);

        var dir = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "pairing-preview.png"));
        }
    }

    [AvaloniaFact]
    public void Dashboard_renders_by_default()
    {
        // A genuinely fresh, unauthenticated shell defaults to Pairing (Task 3's device-identity
        // gate). The preview simulates an already-bootstrapped device, so that same gate
        // immediately unlocks and auto-advances the selection off Pairing onto the dashboard.
        var viewModel = MainWindowViewModel.CreatePreview();
        var window = new MainWindow { DataContext = viewModel };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(viewModel.IsDashboard);
    }

    [AvaloniaFact]
    public void Settings_page_renders_its_connected_controls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonic-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsViewModel(
                "https://backend.example/",
                new RelayPreferenceStore(Path.Combine(dir, "p.json")),
                new AudioQualityStore(Path.Combine(dir, "q.json")));
            var window = new Window
            {
                Width = 700,
                Height = 500,
                Content = new SettingsView { DataContext = settings },
            };

            window.Show();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var dest = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
            if (!string.IsNullOrWhiteSpace(dest))
            {
                Directory.CreateDirectory(dest);
                frame!.Save(Path.Combine(dest, "settings-preview.png"));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public void Dashboard_console_scrolls_with_the_cards_and_keeps_its_height_cap()
    {
        // The console lives inside the dashboard's ScrollViewer (it used to be docked to the
        // window bottom, where it painted over the Signal Infrastructure card at narrow sizes)
        // and the dashboard instance still caps its height. Diagnostics deliberately does not:
        // its console fills the page.
        var viewModel = MainWindowViewModel.CreatePreview();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Assert.True(viewModel.IsDashboard);

        var console = window.GetVisualDescendants()
            .OfType<SonicRelay.Windows.Desktop.Controls.TechnicalConsole>()
            .First(candidate => candidate.IsEffectivelyVisible);

        Assert.True(double.IsFinite(console.MaxHeight),
            "The dashboard console must cap its height so it cannot dominate the page.");
        Assert.True(console.GetVisualAncestors().OfType<ScrollViewer>().Any(),
            "The dashboard console must scroll with the cards instead of overlaying them.");
    }

    [AvaloniaFact]
    public void Badge_converter_resolves_semantic_token_brushes()
    {
        var brush = DashboardBadgeToBrushConverter.Instance.Convert(
            DashboardBadge.Success, typeof(IBrush), "Foreground", CultureInfo.InvariantCulture);

        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        // Sonic.SuccessBrush is the locked teal #4DEFD6.
        Assert.Equal(Color.Parse("#4DEFD6"), solid.Color);
    }
}
