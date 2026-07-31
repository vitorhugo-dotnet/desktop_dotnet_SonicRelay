using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop;

/// <summary>
/// Wires the platform-agnostic <see cref="TrayApplicationController"/> to an Avalonia
/// <see cref="TrayIcon"/> and the main window (issue #32): a status tooltip and context menu
/// built from the publisher snapshot, minimise/close-to-tray while a session runs, and a
/// tray "Reconnect signaling" action. All decisions come from the tested controller; this
/// type only performs the UI side effects (show/hide, clipboard, shutdown).
/// </summary>
public sealed class DesktopTrayController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly Window window;
    private readonly MainWindowViewModel viewModel;
    private readonly TrayApplicationController controller;
    private readonly TrayIcon trayIcon;
    private bool quitting;

    public DesktopTrayController(
        IClassicDesktopStyleApplicationLifetime lifetime,
        Window window,
        MainWindowViewModel viewModel)
    {
        this.lifetime = lifetime;
        this.window = window;
        this.viewModel = viewModel;
        controller = new TrayApplicationController(() => viewModel.KeepRunningInTray);

        trayIcon = new TrayIcon
        {
            Icon = TryCreateIcon(),
            ToolTipText = controller.TooltipFor(viewModel.CurrentSnapshot),
            IsVisible = true,
        };

        // Register with the platform tray backend before wiring any subscriptions
        // below: if the desktop environment has no tray support, this throws, and
        // the constructor must leave nothing subscribed — otherwise a half-constructed
        // instance stays alive via a dangling window.Closing/PropertyChanged handler,
        // and a later "hide to tray" decision hides the window with no visible tray
        // icon to bring it back (an unreachable hidden process — see
        // docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
        // "Tray and lifecycle").
        TrayIcon.SetIcons(Application.Current!, [trayIcon]);

        trayIcon.Clicked += (_, _) => ShowWindow();
        viewModel.Changed += Refresh;
        window.Closing += OnWindowClosing;
        window.PropertyChanged += OnWindowPropertyChanged;

        Refresh();
    }

    private void Refresh()
    {
        var snapshot = viewModel.CurrentSnapshot;
        trayIcon.ToolTipText = controller.TooltipFor(snapshot);
        trayIcon.Menu = BuildMenu(snapshot);
    }

    private NativeMenu BuildMenu(PublisherSnapshot? snapshot)
    {
        var menu = new NativeMenu();
        foreach (var item in controller.BuildMenu(snapshot))
        {
            var menuItem = new NativeMenuItem(item.Label) { IsEnabled = item.Enabled };
            var command = item.Command;
            menuItem.Click += (_, _) => Dispatch(command);
            menu.Add(menuItem);
        }
        return menu;
    }

    private void Dispatch(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
            case TrayCommand.Status:
                ShowWindow();
                break;
            case TrayCommand.StartStream:
                Execute(viewModel.StartAudioCommand);
                break;
            case TrayCommand.StopStream:
                Execute(viewModel.StopAudioCommand);
                break;
            case TrayCommand.ReconnectSignaling:
                Execute(viewModel.RetryCommand);
                break;
            case TrayCommand.CopySessionCode:
                _ = CopySessionCodeAsync();
                break;
            case TrayCommand.Quit:
                quitting = true;
                lifetime.Shutdown();
                break;
        }
    }

    private static void Execute(RelayCommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private async Task CopySessionCodeAsync()
    {
        if (!viewModel.Shell.HasSessionCode) return;
        var clipboard = window.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(viewModel.Shell.SessionCodeText);
    }

    private void ShowWindow()
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // A deliberate Quit (menu or shutdown) closes for real; otherwise honour the tray policy.
        if (quitting) return;
        if (controller.DecideOnClose(viewModel.CurrentSnapshot) == TrayCloseDecision.Hide)
        {
            e.Cancel = true;
            window.Hide();
            viewModel.LogDiagnostic("window-state", "Window close intercepted; kept running in tray.");
        }
        else
        {
            viewModel.LogDiagnostic("window-state", "Window closing for real (no active session to keep alive).");
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty || (WindowState)e.NewValue! != WindowState.Minimized)
            return;
        if (controller.DecideOnMinimize() == TrayCloseDecision.Hide)
        {
            // Restore the stored state so the next Show opens normally rather than minimised.
            window.WindowState = WindowState.Normal;
            window.Hide();
            viewModel.LogDiagnostic("window-state", "Minimized to tray.");
        }
    }

    public void Dispose()
    {
        viewModel.Changed -= Refresh;
        window.Closing -= OnWindowClosing;
        window.PropertyChanged -= OnWindowPropertyChanged;
        trayIcon.IsVisible = false;
        trayIcon.Dispose();
    }

    // A small self-contained icon (teal ring on a blue tile) so the tray needs no asset file.
    private static WindowIcon? TryCreateIcon()
    {
        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#2F5FA8")), null,
                    new RoundedRect(new Rect(0, 0, 64, 64), 14));
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#4DEFD6")), null,
                    new Point(32, 32), 13, 13);
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#2F5FA8")), null,
                    new Point(32, 32), 6, 6);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream);
            stream.Position = 0;
            return new WindowIcon(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Rendering an icon must never stop the app from starting; the tray shows a default.
            return null;
        }
    }
}
