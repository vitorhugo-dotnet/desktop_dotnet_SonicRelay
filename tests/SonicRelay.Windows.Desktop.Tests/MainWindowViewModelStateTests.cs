using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The shell chooses the sign-in surface vs. the dashboard from the snapshot, so a successful
/// sign-in flips it to the dashboard automatically (#32).
/// </summary>
public sealed class MainWindowViewModelStateTests
{
    [Fact]
    public void Show_login_without_an_authenticated_snapshot()
    {
        Assert.True(MainWindowViewModel.ShouldShowLogin(null));
        Assert.True(MainWindowViewModel.ShouldShowLogin(new PublisherSnapshot { IsAuthenticated = false }));
    }

    [Fact]
    public void Hide_login_once_authenticated()
    {
        Assert.False(MainWindowViewModel.ShouldShowLogin(new PublisherSnapshot { IsAuthenticated = true }));
    }

    [Fact]
    public void Fresh_view_model_opens_on_the_login_surface()
    {
        var vm = new MainWindowViewModel();

        Assert.True(vm.ShowLogin);
    }

    [Fact]
    public void Preview_view_model_opens_on_the_dashboard()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.False(vm.ShowLogin);
        Assert.False(vm.Auth.HasError);
    }

    [Fact]
    public void Navigation_defaults_to_the_dashboard()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.True(vm.IsDashboard);
        Assert.False(vm.IsSession);
        Assert.False(vm.IsDiagnostics);
    }

    [Fact]
    public void Selecting_a_destination_switches_the_current_page()
    {
        var vm = new MainWindowViewModel();
        var session = vm.Navigation.Single(item => item.Key == PageKey.Session);

        vm.SelectedNavigation = session;

        Assert.Equal(PageKey.Session, vm.CurrentPage);
        Assert.True(vm.IsSession);
        Assert.False(vm.IsDashboard);
        Assert.Equal("Session", vm.PageTitle);
    }

    [Fact]
    public void All_destinations_are_navigable()
    {
        var vm = new MainWindowViewModel();

        Assert.All(vm.Navigation, item => Assert.True(item.IsEnabled));
    }

    [Fact]
    public void Audio_and_settings_are_disconnected_without_a_runtime()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.Settings.IsConnected);
        Assert.False(vm.Audio.IsConnected);
    }

    [Fact]
    public void Fresh_view_model_does_not_keep_running_in_tray()
    {
        var vm = new MainWindowViewModel();

        // Logged out: closing the window should not keep the app alive in the tray.
        Assert.False(vm.KeepRunningInTray);
        Assert.Null(vm.CurrentSnapshot);
    }

    [Fact]
    public void A_streaming_preview_keeps_running_in_tray()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.True(vm.KeepRunningInTray);
    }

    [Fact]
    public void A_null_selection_keeps_the_last_page()
    {
        var vm = new MainWindowViewModel();
        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Diagnostics);

        vm.SelectedNavigation = null!;

        Assert.Equal(PageKey.Diagnostics, vm.CurrentPage);
    }
}
