using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SonicRelay.Windows.ApiClient.DeviceIdentity;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Presentation.Pairing;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Pairing (and Settings) stay reachable even while every other destination is locked behind
/// Task 3's device-identity gate — that was the fix for the old full-shell gate, which hid
/// Settings too and left a bad backend URL with no way back in. Unpair still has to defend
/// against the gate on its own terms, though: an unpair's automatic re-bootstrap flips the
/// gate open through the same StateChanged path a normal cold start does, and without
/// <c>MainWindowViewModel</c>'s pairing-held-by-unpair latch that would silently bounce the
/// shell back to the dashboard before the user ever saw the fresh pairing code.
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
    public void Without_a_device_identity_only_pairing_and_settings_are_reachable()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.HasDeviceIdentity);
        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
        Assert.True(vm.Navigation.Single(item => item.Key == PageKey.Pairing).IsEnabled);
        Assert.True(vm.Navigation.Single(item => item.Key == PageKey.Settings).IsEnabled);
        Assert.All(
            vm.Navigation.Where(item => item.Key is not (PageKey.Pairing or PageKey.Settings)),
            item => Assert.False(item.IsEnabled));
    }

    [Fact]
    public void A_bootstrapped_device_identity_unlocks_the_shell_and_opens_the_dashboard()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.True(vm.HasDeviceIdentity);
        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
        Assert.All(vm.Navigation, item => Assert.True(item.IsEnabled));
    }

    [Fact]
    public void Pairing_stays_reachable_after_the_shell_unlocks()
    {
        var vm = MainWindowViewModel.CreatePreview();

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Pairing);

        Assert.True(vm.IsPairing);
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

    [AvaloniaFact]
    public async Task Signing_out_selects_the_pairing_page_even_if_rebootstrap_immediately_succeeds()
    {
        // A fake device-identity API client plus an in-memory credential store are the
        // narrowest seam that lets the re-bootstrap InitializeDeviceIdentityAsync runs after
        // unpair genuinely SUCCEED, rather than always throwing (a prior version of this test
        // pointed at "https://backend.example.test/" alone, a reserved TLD guaranteed to fail
        // DNS, so the re-bootstrap always threw, was swallowed by UnpairAsync's catch, and
        // HasDeviceIdentity never flipped true — this test could not actually distinguish
        // "stayed on Pairing because the gate was correctly held off" from "stayed on Pairing
        // because bootstrap never got the chance to try moving it away"). [AvaloniaFact] plus
        // the explicit Dispatcher.UIThread.RunJobs() below (the same pattern
        // PairingViewLifecycleTests uses) is needed because OnStateChanged marshals the
        // resulting HasDeviceIdentity/ApplyShellGate update through Dispatcher.UIThread.Post —
        // without a real dispatcher pumped on this thread that post is not guaranteed to have
        // run yet when the assertions below execute, which is exactly the gate-skip behaviour
        // under test here.
        var deviceIdentityApi = new FakeDeviceIdentityApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            credentialStoreOverride: new InMemoryFakeDeviceCredentialStore(),
            relayPreferenceOverride: CreateTempRelayPreference(),
            deviceIdentityApiClientOverride: deviceIdentityApi);
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Audio);

        await vm.UnpairAsync();
        await vm.UnpairAsync();
        Dispatcher.UIThread.RunJobs();

        // The re-bootstrap really did succeed this time (not merely swallowed) ...
        Assert.True(vm.HasDeviceIdentity);
        // ... yet the shell gate did not bounce the selection off Pairing onto the dashboard the
        // instant it succeeded, the way it does for a normal cold start.
        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task A_normal_cold_start_still_advances_off_pairing_once_an_identity_is_gained()
    {
        // The counterpart to the test above: an identity gained without ever going through
        // Unpair (the ordinary case — an existing credential loads at startup, or an
        // unauthenticated device bootstraps for the first time) must still auto-advance the
        // selection off Pairing, exactly as before Unpair's pairing-held-by-unpair latch existed.
        var deviceIdentityApi = new FakeDeviceIdentityApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            credentialStoreOverride: new InMemoryFakeDeviceCredentialStore(),
            relayPreferenceOverride: CreateTempRelayPreference(),
            deviceIdentityApiClientOverride: deviceIdentityApi);
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        Assert.Equal(PageKey.Pairing, vm.CurrentPage);

        await runtime.InitializeDeviceIdentityAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasDeviceIdentity);
        Assert.Equal(PageKey.Dashboard, vm.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Rebootstrap_replaces_the_pairing_view_model_exposed_to_the_shell()
    {
        var deviceIdentityApi = new FakeDeviceIdentityApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            credentialStoreOverride: new InMemoryFakeDeviceCredentialStore(),
            relayPreferenceOverride: CreateTempRelayPreference(),
            deviceIdentityApiClientOverride: deviceIdentityApi);
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);

        await runtime.InitializeDeviceIdentityAsync();
        Dispatcher.UIThread.RunJobs();
        var firstPairing = Assert.IsType<PairingViewModel>(vm.Pairing);
        Assert.Same(runtime.Pairing, firstPairing);

        await vm.UnpairAsync();
        await vm.UnpairAsync();
        Dispatcher.UIThread.RunJobs();

        var replacementPairing = Assert.IsType<PairingViewModel>(vm.Pairing);
        Assert.Same(runtime.Pairing, replacementPairing);
        Assert.NotSame(firstPairing, replacementPairing);
    }

    [AvaloniaFact]
    public async Task Rebootstrap_removes_the_old_pairing_view_model_while_identity_is_pending()
    {
        var deviceIdentityApi = new FakeDeviceIdentityApiClient();
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            credentialStoreOverride: new InMemoryFakeDeviceCredentialStore(),
            relayPreferenceOverride: CreateTempRelayPreference(),
            deviceIdentityApiClientOverride: deviceIdentityApi);
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);
        await runtime.InitializeDeviceIdentityAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Pairing);

        var pendingBootstrap = new TaskCompletionSource<BootstrapDeviceResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        deviceIdentityApi.PendingBootstrap = pendingBootstrap;
        deviceIdentityApi.BootstrapStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await vm.UnpairAsync();
        var rebootstrap = vm.UnpairAsync();
        await deviceIdentityApi.BootstrapStarted.Task;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(runtime.Pairing);
        Assert.Null(vm.Pairing);

        pendingBootstrap.SetResult(
            new BootstrapDeviceResponse(Guid.NewGuid(), "replacement-secret", 1));
        await rebootstrap;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(runtime.Pairing, vm.Pairing);
        Assert.NotNull(vm.Pairing);
    }

    [Fact]
    public void Unpair_requires_a_confirmation_before_it_acts()
    {
        var vm = MainWindowViewModel.CreatePreview();

        Assert.False(vm.UnpairConfirmationArmed);
        vm.ArmUnpair();
        Assert.True(vm.UnpairConfirmationArmed);
        vm.DisarmUnpair();
        Assert.False(vm.UnpairConfirmationArmed);
    }

    [Fact]
    public void Navigation_defaults_to_pairing_without_a_device_identity()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(PageKey.Pairing, vm.CurrentPage);
        Assert.True(vm.IsPairing);
        Assert.False(vm.IsDashboard);
        Assert.False(vm.IsDiagnostics);
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
    public async Task Selecting_settings_does_not_touch_the_network()
    {
        // Regression guard for the removal of the old server-sync trigger (issue #26 follow-up
        // — relay mode/coturn override are per-device local preferences now, read straight from
        // RelayPreferenceStore, so opening Settings has nothing to fetch). This only needs to
        // prove opening Settings doesn't throw with a runtime attached but no device identity.
        await using var runtime = PublisherRuntime.Create(
            new Uri("https://backend.example.test/"), new FakeAudio(),
            relayPreferenceOverride: CreateTempRelayPreference());
        var vm = new MainWindowViewModel();
        vm.Attach(runtime);

        vm.SelectedNavigation = vm.Navigation.Single(item => item.Key == PageKey.Settings);

        Assert.True(vm.IsSettings);
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

    /// <summary>Always succeeds, immediately, with no network call — the seam that lets a
    /// device-identity bootstrap be driven to a genuine success in a test.</summary>
    private sealed class FakeDeviceIdentityApiClient : IDeviceIdentityApiClient
    {
        public TaskCompletionSource<BootstrapDeviceResponse>? PendingBootstrap { get; set; }
        public TaskCompletionSource? BootstrapStarted { get; set; }

        public Task<BootstrapDeviceResponse> BootstrapAsync(
            BootstrapDeviceRequest request, CancellationToken cancellationToken = default)
        {
            if (PendingBootstrap is not { } pending)
                return Task.FromResult(new BootstrapDeviceResponse(Guid.NewGuid(), "device-secret", 1));

            PendingBootstrap = null;
            BootstrapStarted?.SetResult();
            return pending.Task;
        }

        public Task<DeviceTokenResponse> TokenAsync(
            DeviceTokenRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceTokenResponse("device-token", DateTimeOffset.UtcNow.AddHours(1), []));
    }

    private sealed class InMemoryFakeDeviceCredentialStore : IDeviceCredentialStore
    {
        private DeviceCredential? stored;

        public Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            stored = credential;
            return Task.FromResult(DeviceCredentialStorageResult.Success(credential));
        }

        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(stored is null ? DeviceCredentialStorageResult.Success() : DeviceCredentialStorageResult.Success(stored));

        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
        {
            stored = null;
            return Task.FromResult(DeviceCredentialStorageResult.Success());
        }
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
    public void All_destinations_are_navigable_once_the_shell_unlocks()
    {
        var vm = MainWindowViewModel.CreatePreview();

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
