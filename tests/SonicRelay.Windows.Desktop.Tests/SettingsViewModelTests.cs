using SonicRelay.Windows.ApiClient.Settings;
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
            new SonicRelay.Windows.Core.Configuration.RelayPreferenceStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-{Guid.NewGuid():N}.json")),
            new SonicRelay.Windows.Core.Audio.AudioQualityStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-test-quality-{Guid.NewGuid():N}.json")),
            changeBackendUrl);
}

public sealed class SettingsViewModelRelaySettingsTests
{
    [Fact]
    public async Task Refresh_applies_the_servers_relay_mode_and_turn_uri()
    {
        var api = new StubRelaySettingsApiClient(
            get: new RelaySettingsResponse("forceRelay", ["turn:mine.example.com:3478"], true));
        var vm = MakeConnectedViewModel(api);

        await vm.RefreshRelaySettingsAsync();

        Assert.Equal("forceRelay", vm.RelayMode);
        Assert.Equal("turn:mine.example.com:3478", vm.TurnUriInput);
        Assert.Null(vm.RelaySettingsError);
    }

    [Fact]
    public async Task Saving_the_relay_mode_writes_through_to_the_server_and_applies_the_response()
    {
        var api = new StubRelaySettingsApiClient(
            update: new RelaySettingsResponse("disableFallback", [], false));
        var vm = MakeConnectedViewModel(api);
        vm.RelayMode = "disableFallback";

        await vm.SaveRelayModeAsync();

        Assert.Equal("disableFallback", api.LastUpdateRequest!.RelayMode);
        Assert.Equal("disableFallback", vm.RelayMode);
    }

    [Fact]
    public async Task Saving_the_turn_uri_sends_it_as_a_single_element_list()
    {
        var api = new StubRelaySettingsApiClient(
            update: new RelaySettingsResponse("automatic", ["turn:new.example.com:3478"], false));
        var vm = MakeConnectedViewModel(api);
        vm.TurnUriInput = "turn:new.example.com:3478";

        await vm.SaveTurnUriAsync();

        Assert.Equal(["turn:new.example.com:3478"], api.LastUpdateRequest!.TurnUris);
    }

    [Fact]
    public void Coturn_field_is_hidden_until_the_device_has_an_identity()
    {
        var vm = MakeConnectedViewModel(new StubRelaySettingsApiClient());

        Assert.False(vm.HasDeviceIdentity);

        vm.UpdateAuthentication(true);

        Assert.True(vm.HasDeviceIdentity);
    }

    private static SettingsViewModel MakeConnectedViewModel(IRelaySettingsApiClient api) =>
        new(
            "https://backend.example.test/",
            new SonicRelay.Windows.Core.Configuration.RelayPreferenceStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-relay-test-{Guid.NewGuid():N}.json")),
            new SonicRelay.Windows.Core.Audio.AudioQualityStore(
                Path.Combine(Path.GetTempPath(), $"sonicrelay-settings-vm-relay-test-quality-{Guid.NewGuid():N}.json")),
            api,
            _ => Task.FromResult<string?>(null));

    private sealed class StubRelaySettingsApiClient(
        RelaySettingsResponse? get = null, RelaySettingsResponse? update = null) : IRelaySettingsApiClient
    {
        public UpdateRelaySettingsRequest? LastUpdateRequest { get; private set; }

        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(get ?? new RelaySettingsResponse("automatic", [], false));

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult(update ?? new RelaySettingsResponse("automatic", [], false));
        }
    }
}
