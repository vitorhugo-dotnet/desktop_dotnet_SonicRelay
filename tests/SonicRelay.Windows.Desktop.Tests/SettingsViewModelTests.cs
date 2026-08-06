using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Settings edits must flow into the shared preference stores the WebRTC factory reads (#32).
/// Uses temp-path stores so no real user configuration is touched.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sonic-settings-" + Guid.NewGuid().ToString("N"));

    private (RelayPreferenceStore Relay, AudioQualityStore Quality) Stores()
    {
        Directory.CreateDirectory(dir);
        return (new RelayPreferenceStore(Path.Combine(dir, "prefs.json")),
                new AudioQualityStore(Path.Combine(dir, "quality.json")));
    }

    [Fact]
    public void Default_instance_is_disconnected_and_readonly()
    {
        var vm = new SettingsViewModel();

        Assert.False(vm.IsConnected);
        Assert.Equal("—", vm.BackendUrl);
    }

    [Fact]
    public void Connected_instance_reflects_the_stores()
    {
        var (relay, quality) = Stores();

        var vm = new SettingsViewModel("https://backend.example/", relay, quality);

        Assert.True(vm.IsConnected);
        Assert.Equal("https://backend.example/", vm.BackendUrl);
        Assert.Equal(quality.CurrentProfile.Id, vm.SelectedProfile.Id);
        Assert.Contains(AudioQualityProfile.Voice, vm.Profiles);
    }

    [Fact]
    public void Selecting_a_profile_updates_the_store()
    {
        var (relay, quality) = Stores();
        var vm = new SettingsViewModel("https://backend.example/", relay, quality);

        vm.SelectedProfile = AudioQualityProfile.Voice;

        Assert.Equal(AudioQualityProfile.Voice.Id, quality.CurrentProfile.Id);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

public sealed class SettingsViewModelBackendUrlTests
{
    [Fact]
    public async Task Save_rejects_a_non_absolute_url_without_calling_the_change_delegate()
    {
        var called = false;
        var vm = MakeConnectedViewModel(url =>
        {
            called = true;
            return Task.FromResult<string?>(null);
        });
        vm.BackendUrlInput = "not-a-url";

        await vm.SaveBackendUrlAsync();

        Assert.False(called);
        Assert.NotNull(vm.BackendUrlError);
    }

    [Fact]
    public async Task Save_surfaces_the_error_the_change_delegate_returns()
    {
        var vm = MakeConnectedViewModel(_ => Task.FromResult<string?>("Backend unreachable."));
        vm.BackendUrlInput = "https://new-backend.example.test/";

        await vm.SaveBackendUrlAsync();

        Assert.Equal("Backend unreachable.", vm.BackendUrlError);
    }

    [Fact]
    public async Task Successful_save_clears_any_previous_error()
    {
        var vm = MakeConnectedViewModel(_ => Task.FromResult<string?>(null));
        vm.BackendUrlInput = "https://good-backend.example.test/";

        await vm.SaveBackendUrlAsync();

        Assert.Null(vm.BackendUrlError);
    }

    private static SettingsViewModel MakeConnectedViewModel(Func<string, Task<string?>> changeBackendUrl) =>
        new(
            "https://old-backend.example.test/",
            new RelayPreferenceStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-{Guid.NewGuid():N}.json")),
            new AudioQualityStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-quality-{Guid.NewGuid():N}.json")),
            changeBackendUrl);
}

/// <summary>
/// Relay mode and the coturn override are per-device local preferences (issue #26 follow-up —
/// the backend row these used to sync through was global to the whole deployment, so one
/// device editing the coturn URL changed the relay for every other device). These tests cover
/// the local-store round trip and the "never pre-filled with the backend's value" design point
/// directly, replacing the old server-sync tests (refresh/save-through-to-server, an unfetched
/// gate on the save command) that no longer apply now that the backend endpoint is gone.
/// </summary>
public sealed class SettingsViewModelRelayPreferenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sonic-settings-relay-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saving_the_relay_mode_writes_straight_through_to_the_local_store()
    {
        var (relay, vm) = MakeConnectedViewModel();
        vm.RelayMode = RelayModes.DisableFallback;

        await vm.SaveRelayModeAsync();

        Assert.Equal(RelayModes.DisableFallback, relay.RelayMode);
    }

    [Fact]
    public async Task Saving_the_turn_uri_writes_straight_through_to_the_local_store()
    {
        var (relay, vm) = MakeConnectedViewModel();
        vm.TurnUriInput = "turn:mine.example.com:3478";

        await vm.SaveTurnUriAsync();

        Assert.Equal("turn:mine.example.com:3478", relay.CoturnUrlOverride);
    }

    [Fact]
    public async Task A_blank_turn_uri_clears_the_override()
    {
        var (relay, vm) = MakeConnectedViewModel();
        await relay.SetCoturnUrlOverrideAsync("turn:mine.example.com:3478");
        vm.TurnUriInput = "   ";

        await vm.SaveTurnUriAsync();

        Assert.Null(relay.CoturnUrlOverride);
    }

    [Fact]
    public void The_coturn_field_never_starts_prefilled_with_a_backend_value()
    {
        // Design point: the field starts blank unless the user set their own override before —
        // it must never disclose a value the app itself fetched from a server, because there is
        // no such fetch any more.
        var (_, vm) = MakeConnectedViewModel();

        Assert.Equal("", vm.TurnUriInput);
    }

    [Fact]
    public void TurnUriInput_starts_from_the_stores_own_prior_override()
    {
        var relayPath = Path.Combine(dir, "prefs.json");
        Directory.CreateDirectory(dir);
        var seeded = new RelayPreferenceStore(relayPath);
        seeded.SetCoturnUrlOverrideAsync("turn:previously-saved.example.com:3478").GetAwaiter().GetResult();

        var vm = new SettingsViewModel(
            "https://backend.example.test/",
            new RelayPreferenceStore(relayPath),
            new AudioQualityStore(Path.Combine(dir, "quality.json")));

        Assert.Equal("turn:previously-saved.example.com:3478", vm.TurnUriInput);
    }

    [Fact]
    public void Coturn_field_is_hidden_until_the_device_has_an_identity()
    {
        var (_, vm) = MakeConnectedViewModel();

        Assert.False(vm.HasDeviceIdentity);

        vm.UpdateAuthentication(true);

        Assert.True(vm.HasDeviceIdentity);
    }

    [Fact]
    public void Save_turn_uri_canExecute_requires_a_device_identity_but_nothing_else()
    {
        var (_, vm) = MakeConnectedViewModel();

        Assert.False(vm.SaveTurnUriCommand.CanExecute(null));

        vm.UpdateAuthentication(true);

        Assert.True(vm.SaveTurnUriCommand.CanExecute(null));
    }

    private (RelayPreferenceStore Relay, SettingsViewModel ViewModel) MakeConnectedViewModel()
    {
        Directory.CreateDirectory(dir);
        var relay = new RelayPreferenceStore(Path.Combine(dir, "prefs.json"));
        var vm = new SettingsViewModel(
            "https://backend.example.test/",
            relay,
            new AudioQualityStore(Path.Combine(dir, "quality.json")),
            _ => Task.FromResult<string?>(null));
        return (relay, vm);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
