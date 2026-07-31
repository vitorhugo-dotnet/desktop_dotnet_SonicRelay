using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The audio page surfaces the platform output devices and persists the selection to the
/// enumerator + preference store, applying to the next capture start (#32).
/// </summary>
public sealed class AudioPageViewModelTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sonic-audio-" + Guid.NewGuid().ToString("N"));

    private AudioOutputPreferenceStore Store()
    {
        Directory.CreateDirectory(dir);
        return new AudioOutputPreferenceStore(Path.Combine(dir, "audio-output.json"));
    }

    private sealed class FakeEnumerator : IAudioDeviceEnumerator
    {
        public string? PreferredDeviceId { get; set; }
        public List<AudioOutputDevice> Devices { get; } = [];
        public string? LastSelected { get; private set; }
        public bool SelectCalled { get; private set; }

        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => Devices;

        public void SelectOutputDevice(string? deviceId)
        {
            SelectCalled = true;
            LastSelected = deviceId;
            PreferredDeviceId = deviceId;
        }
    }

    [Fact]
    public void Default_instance_is_disconnected_with_no_devices()
    {
        var vm = new AudioPageViewModel();

        Assert.False(vm.IsConnected);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public void Connected_instance_lists_system_default_first_then_endpoints()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Devices.Add(new AudioOutputDevice("dev-1", "Speakers", IsDefault: false));

        var vm = new AudioPageViewModel(enumerator, Store());

        Assert.True(vm.IsConnected);
        Assert.Equal(AudioPageViewModel.SystemDefaultId, vm.Devices[0].Id);
        Assert.Contains(vm.Devices, device => device.Id == "dev-1");
        // No preferred id → the system-default entry is selected.
        Assert.Equal(AudioPageViewModel.SystemDefaultId, vm.SelectedDevice!.Id);
    }

    [Fact]
    public void Selecting_an_endpoint_applies_it_to_the_enumerator_and_store()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Devices.Add(new AudioOutputDevice("dev-1", "Speakers", IsDefault: false));
        var store = Store();
        var vm = new AudioPageViewModel(enumerator, store);

        vm.SelectedDevice = vm.Devices.Single(device => device.Id == "dev-1");

        Assert.True(enumerator.SelectCalled);
        Assert.Equal("dev-1", enumerator.LastSelected);
        Assert.Equal("dev-1", store.SelectedDeviceId);
    }

    [Fact]
    public void Selecting_system_default_clears_the_device_to_null()
    {
        var enumerator = new FakeEnumerator { PreferredDeviceId = "dev-1" };
        enumerator.Devices.Add(new AudioOutputDevice("dev-1", "Speakers", IsDefault: false));
        var vm = new AudioPageViewModel(enumerator, Store());

        vm.SelectedDevice = vm.Devices.Single(device => device.Id == AudioPageViewModel.SystemDefaultId);

        Assert.True(enumerator.SelectCalled);
        Assert.Null(enumerator.LastSelected);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
