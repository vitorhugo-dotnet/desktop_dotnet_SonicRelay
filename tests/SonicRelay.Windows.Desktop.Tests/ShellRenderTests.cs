using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
    public void Login_surface_renders_for_an_unauthenticated_shell()
    {
        var window = new MainWindow
        {
            // Fresh view model with no runtime: ShowLogin is true, so the sign-in surface shows.
            DataContext = new MainWindowViewModel(),
        };

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var dir = Environment.GetEnvironmentVariable("SHELL_SHOT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "login-preview.png"));
        }
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
    public void Badge_converter_resolves_semantic_token_brushes()
    {
        var brush = DashboardBadgeToBrushConverter.Instance.Convert(
            DashboardBadge.Success, typeof(IBrush), "Foreground", CultureInfo.InvariantCulture);

        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        // Sonic.SuccessBrush is the locked teal #4DEFD6.
        Assert.Equal(Color.Parse("#4DEFD6"), solid.Color);
    }
}
