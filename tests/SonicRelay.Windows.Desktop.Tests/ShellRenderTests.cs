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
    public void Session_page_renders_when_selected()
    {
        var viewModel = MainWindowViewModel.CreatePreview();
        viewModel.SelectedNavigation = viewModel.Navigation.Single(item => item.Key == PageKey.Session);
        var window = new MainWindow { DataContext = viewModel };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(viewModel.IsSession);

        var dir = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "session-preview.png"));
        }
    }

    [AvaloniaFact]
    public void Pairing_surface_renders_when_the_pairing_page_is_selected()
    {
        // Pairing is a normal, always-reachable nav page now (issue #26 follow-up), not a
        // full-shell gate — selecting it explicitly shows the pairing surface.
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
        // A fresh shell (and the preview) opens on the dashboard by default now that Pairing
        // is an ordinary nav page rather than a snapshot-derived gate (issue #26 follow-up).
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
    public void Technical_console_is_height_bounded_so_it_cannot_cover_the_dashboard_cards()
    {
        var console = new SonicRelay.Windows.Desktop.Controls.TechnicalConsole
        {
            DataContext = new DashboardShellViewModel()
        };

        // Compiled-binding content only materializes past the UserControl's ContentPresenter
        // once the control is attached to a shown visual tree, so this uses the same
        // window.Show() headless-render setup the rest of this file relies on.
        var window = new Window { Content = console };
        window.Show();

        var card = console.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("card"));

        Assert.True(double.IsFinite(card.MaxHeight),
            "The console must cap its height or the DockPanel lets it grow over the cards above.");
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
