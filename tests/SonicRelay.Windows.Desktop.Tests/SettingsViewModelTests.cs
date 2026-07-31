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
