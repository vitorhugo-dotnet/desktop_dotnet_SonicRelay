using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Audio surface (issue #32): picks which system output endpoint to capture. It surfaces the
/// platform device enumerator (WASAPI render endpoints today, PipeWire sinks later) and
/// persists the choice, applying to the next capture start. Without an attached runtime it is
/// <see cref="IsConnected"/> = false and read-only.
/// </summary>
public sealed class AudioPageViewModel : ViewModelBase
{
    /// <summary>Sentinel id for the "system default" entry (null device id to the enumerator).</summary>
    public const string SystemDefaultId = "";

    private readonly IAudioDeviceEnumerator? enumerator;
    private readonly AudioOutputPreferenceStore? store;
    private AudioOutputDevice? selectedDevice;

    /// <summary>Disconnected state — no runtime attached.</summary>
    public AudioPageViewModel()
    {
        Devices = [];
    }

    public AudioPageViewModel(IAudioDeviceEnumerator enumerator, AudioOutputPreferenceStore store)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(store);
        this.enumerator = enumerator;
        this.store = store;
        IsConnected = true;

        var devices = new List<AudioOutputDevice>
        {
            new(SystemDefaultId, "System default", IsDefault: true),
        };
        devices.AddRange(enumerator.GetOutputDevices());
        Devices = devices;

        var preferred = enumerator.PreferredDeviceId ?? SystemDefaultId;
        selectedDevice = devices.FirstOrDefault(device => device.Id == preferred) ?? devices[0];
    }

    public bool IsConnected { get; }
    public IReadOnlyList<AudioOutputDevice> Devices { get; }

    public AudioOutputDevice? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (value is null || !SetProperty(ref selectedDevice, value) || enumerator is null || store is null)
                return;
            var deviceId = value.Id == SystemDefaultId ? null : value.Id;
            enumerator.SelectOutputDevice(deviceId);
            Persist(store.SetSelectedDeviceAsync(deviceId, deviceId is null ? null : value.Name));
        }
    }

    private static async void Persist(Task write)
    {
        try
        {
            await write;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Best-effort persistence; the enumerator already has the selection for the next start.
        }
    }
}
