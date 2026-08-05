using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Pairing is a normal, always-reachable nav page (issue #26 follow-up) — it is no longer a
/// full-shell gate keyed off device-identity bootstrap, which is what let a sign-out's
/// automatic re-bootstrap silently flip the shell back to the dashboard before the user ever
/// saw the fresh pairing code.
/// </summary>
public sealed class MainWindowViewModelStateTests
{
    [Fact]
    public void Navigation_includes_a_pairing_destination()
    {
        var vm = new MainWindowViewModel();

        Assert.Contains(vm.Navigation, item => item.Key == PageKey.Pairing);
    }

    [Fact]
    public void Fresh_view_model_opens_on_the_dashboard()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public void Preview_view_model_opens_on_the_dashboard()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
    }

    [Fact]
    public async Task Attaching_a_runtime_before_bootstrap_still_renders_with_no_pairing_view_model_yet()
    {
        // PublisherRuntime only creates its PairingViewModel once device-identity bootstrap
        // succeeds; attaching a freshly created runtime (bootstrap not yet run) must not
        // crash and must leave Pairing null until bootstrap completes (issue #26).
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(), relayPreferenceOverride: CreateTempRelayPreference());
        var vm = new MainWindowViewModel();

        vm.Attach(runtime);

        Assert.Null(vm.Pairing);
    }

    [Fact]
    public async Task Signing_out_selects_the_pairing_page_even_if_rebootstrap_immediately_succeeds()
    {
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(), relayPreferenceOverride: CreateTempRelayPreference());
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Session);

        await vm.LogoutAsync();

        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
    }

    [Fact]
    public void Navigation_defaults_to_the_dashboard()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.True(vm.IsDashboard);
        Assert.False(vm.IsSession);
        Assert.False(vm.IsDiagnostics);
        Assert.False(vm.IsPairing);
    }

    [Fact]
    public void Selecting_pairing_switches_the_current_page()
    {
        var vm = new MainWindowViewModel();

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Pairing);

        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
        Assert.True(vm.IsPairing);
        Assert.False(vm.IsDashboard);
    }

    [Fact]
    public void Selecting_pairing_updates_the_top_bar_title()
    {
        // Regression test: PageTitle/PageSubtitle had no PageKey.Pairing arm, so the top bar
        // silently kept showing "Dashboard" while the pairing surface was actually displayed.
        var vm = new MainWindowViewModel();

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Pairing);

        Assert.Equal("Pairing", vm.PageTitle);
        Assert.NotEqual("Live status of the publisher transmission", vm.PageSubtitle);
    }

    [Fact]
    public async Task Selecting_settings_while_authenticated_refreshes_relay_settings()
    {
        // "Settings page opened" is a relay-settings sync trigger point (design spec):
        // otherwise pairing once, changing the relay mode/coturn URL from another app, then
        // opening Windows Settings hours later shows a stale value and silently reverts the
        // other app's change on the next save.
        var stub = new StubRelaySettingsApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            relaySettingsApiOverride: stub, relayPreferenceOverride: CreateTempRelayPreference());
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        // Simulate device-identity bootstrap having already completed; this itself triggers one
        // auto-refresh (a separate fix), so record a baseline before navigating.
        vm.Settings.UpdateAuthentication(true);
        var baseline = stub.GetCallCount;

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Settings);

        Assert.Equal(baseline + 1, stub.GetCallCount);
    }

    [Fact]
    public async Task Selecting_settings_without_an_identity_does_not_call_the_relay_settings_api()
    {
        var stub = new StubRelaySettingsApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            relaySettingsApiOverride: stub, relayPreferenceOverride: CreateTempRelayPreference());
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Settings);

        Assert.Equal(0, stub.GetCallCount);
    }

    // PublisherRuntime.Create falls back to the real, shared RelayPreferenceStore.DefaultPath
    // (%LocalAppData%/SonicRelay/WindowsPublisher/preferences.json) when no override is given.
    // Several tests here trigger a successful relay-settings refresh (via UpdateAuthentication
    // or a Settings navigation), which really does write through to that store — without this
    // override, those writes land on the real file and race with any other test/process on
    // the same machine touching it (this is exactly what made CI flaky: PublisherRuntimeTests
    // in a different test project independently asserts that file's write time never changes).
    private static RelayPreferenceStore CreateTempRelayPreference() =>
        new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "relay-preferences.json"));

    private sealed class StubRelaySettingsApiClient : IRelaySettingsApiClient
    {
        public int GetCallCount { get; private set; }

        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            return Task.FromResult(new RelaySettingsResponse("automatic", [], false));
        }

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RelaySettingsResponse("automatic", [], false));
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public AudioCaptureState State => AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics { get; } = new(AudioCaptureState.Stopped, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId => null;
        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured;
        public event Action<AudioLevelSnapshot>? LevelChanged;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
