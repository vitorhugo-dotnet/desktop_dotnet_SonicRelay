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
            get: new RelaySettingsResponse("automatic", [], false),
            update: new RelaySettingsResponse("automatic", ["turn:new.example.com:3478"], false));
        var vm = MakeConnectedViewModel(api);
        // SaveTurnUriAsync refuses to run until a real server value has been fetched at least
        // once (RelaySettingsLoaded) — see the coturn-wipe-guard tests below.
        await vm.RefreshRelaySettingsAsync();
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

    [Fact]
    public void Device_identity_becoming_available_auto_refreshes_relay_settings()
    {
        // Regression test: TurnUriInput starts as "" and SaveTurnUriAsync maps a blank input to
        // an empty list, so if the coturn field became visible without ever being populated, the
        // very first "Save coturn URL" click would silently wipe the backend's global TURN
        // override for every paired device. UpdateAuthentication(true) — the false-to-true
        // transition, i.e. the moment the coturn field becomes visible — must fetch the real
        // server value first.
        var api = new StubRelaySettingsApiClient(
            get: new RelaySettingsResponse("forceRelay", ["turn:auto.example.com:3478"], true));
        var vm = MakeConnectedViewModel(api);
        Assert.Equal("", vm.TurnUriInput);

        vm.UpdateAuthentication(true);

        Assert.Equal("forceRelay", vm.RelayMode);
        Assert.Equal("turn:auto.example.com:3478", vm.TurnUriInput);
        Assert.Null(vm.RelaySettingsError);
    }

    [Fact]
    public void Repeated_true_updates_do_not_refresh_again()
    {
        var api = new StubRelaySettingsApiClient(
            get: new RelaySettingsResponse("forceRelay", ["turn:auto.example.com:3478"], true));
        var vm = MakeConnectedViewModel(api);
        vm.UpdateAuthentication(true);
        Assert.Equal(1, api.GetCallCount);

        vm.UpdateAuthentication(true);

        Assert.Equal(1, api.GetCallCount);
    }

    [Fact]
    public async Task An_unrecognized_relay_mode_from_the_server_does_not_crash_and_leaves_state_unchanged()
    {
        // Regression test: RelayModes is a closed 3-value set owned by a separately-versioned
        // backend repo that could add a fourth mode someday; that must surface as an error, not
        // an ArgumentException thrown out of RelayPreferenceStore.PersistAsync past this method's
        // callers (an async void RelayCommand.Execute with no catch — an unhandled exception
        // there crashes the whole process).
        var api = new StubRelaySettingsApiClient(
            update: new RelaySettingsResponse("bogus-mode", ["turn:should-not-apply.example.com:3478"], false));
        var vm = MakeConnectedViewModel(api);
        var previousRelayMode = vm.RelayMode;
        var previousTurnUriInput = vm.TurnUriInput;

        var exception = await Record.ExceptionAsync(vm.SaveRelayModeAsync);

        Assert.Null(exception);
        Assert.Equal(previousRelayMode, vm.RelayMode);
        Assert.Equal(previousTurnUriInput, vm.TurnUriInput);
        Assert.NotNull(vm.RelaySettingsError);
    }

    [Fact]
    public async Task A_null_turn_uris_list_from_the_server_is_treated_as_empty_not_a_crash()
    {
        // System.Text.Json deserialization can leave a non-nullable list property null if the
        // backend ever omits the field from the JSON body; .Count on that would NullReferenceException.
        var api = new StubRelaySettingsApiClient(get: new RelaySettingsResponse("automatic", null!, false));
        var vm = MakeConnectedViewModel(api);

        var exception = await Record.ExceptionAsync(vm.RefreshRelaySettingsAsync);

        Assert.Null(exception);
        Assert.Equal("", vm.TurnUriInput);
        Assert.Null(vm.RelaySettingsError);
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_api_client_does_not_escape()
    {
        var api = new ThrowingRelaySettingsApiClient();
        var vm = MakeConnectedViewModel(api);

        var exception = await Record.ExceptionAsync(vm.SaveRelayModeAsync);

        Assert.Null(exception);
        Assert.NotNull(vm.RelaySettingsError);
    }

    [Fact]
    public async Task Saving_the_coturn_url_is_blocked_until_relay_settings_have_loaded_successfully()
    {
        var api = new StubRelaySettingsApiClient();
        var vm = MakeConnectedViewModel(api);
        vm.TurnUriInput = "turn:should-not-be-saved.example.com:3478";
        Assert.False(vm.SaveTurnUriCommand.CanExecute(null));

        await vm.SaveTurnUriAsync();

        Assert.Null(api.LastUpdateRequest);
        Assert.NotNull(vm.RelaySettingsError);
    }

    [Fact]
    public async Task A_failed_auto_refresh_still_blocks_saving_the_coturn_url()
    {
        // Closes the loop on the coturn-wipe fix: the earlier fix made UpdateAuthentication
        // auto-fetch on the success path, but a transient failure (backend briefly unreachable,
        // 401, timeout) left TurnUriInput blank-but-still-saveable, with only an easy-to-miss
        // error message as the only defense.
        var api = new FlakyGetRelaySettingsApiClient();
        var vm = MakeConnectedViewModel(api);

        vm.UpdateAuthentication(true);

        Assert.True(vm.HasDeviceIdentity);
        Assert.NotNull(vm.RelaySettingsError);
        Assert.False(vm.SaveTurnUriCommand.CanExecute(null));

        vm.TurnUriInput = "turn:should-not-be-saved.example.com:3478";
        await vm.SaveTurnUriAsync();

        Assert.Null(api.LastUpdateRequest);
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
        public int GetCallCount { get; private set; }

        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            return Task.FromResult(get ?? new RelaySettingsResponse("automatic", [], false));
        }

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult(update ?? new RelaySettingsResponse("automatic", [], false));
        }
    }

    /// <summary>Throws something other than <c>ApiClientException</c> from every call, to prove
    /// the widened <c>catch (Exception exception) when (exception is not OutOfMemoryException)</c>
    /// clauses catch it too, not just the narrower API-specific exception type.</summary>
    private sealed class ThrowingRelaySettingsApiClient : IRelaySettingsApiClient
    {
        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated unexpected failure");

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated unexpected failure");
    }

    /// <summary>GetAsync always fails; UpdateAsync tracks whether it was ever called (it must
    /// not be, while relay settings have never successfully loaded).</summary>
    private sealed class FlakyGetRelaySettingsApiClient : IRelaySettingsApiClient
    {
        public UpdateRelaySettingsRequest? LastUpdateRequest { get; private set; }

        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated backend outage");

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateRequest = request;
            return Task.FromResult(new RelaySettingsResponse("automatic", [], false));
        }
    }
}
