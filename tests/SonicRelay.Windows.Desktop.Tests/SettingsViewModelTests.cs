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
    public void Toggling_force_relay_updates_the_store()
    {
        var (relay, quality) = Stores();
        var vm = new SettingsViewModel("https://backend.example/", relay, quality);

        vm.ForceRelay = true;

        // The store applies the value synchronously (before the disk write) for the next stream.
        Assert.True(relay.ForceRelay);
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
