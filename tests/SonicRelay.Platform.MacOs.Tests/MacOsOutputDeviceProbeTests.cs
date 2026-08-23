using SonicRelay.Platform.MacOs.Audio;

namespace SonicRelay.Platform.MacOs.Tests;

public sealed class MacOsOutputDeviceProbeTests
{
    /// <summary>
    /// ScreenCaptureKit taps the system output mix and cannot be pointed at a
    /// chosen endpoint, so offering entries here would build a picker that
    /// cannot change what is captured. AudioPageViewModel still shows its own
    /// "System default" entry, which is an accurate description of macOS
    /// capture.
    /// </summary>
    [Fact]
    public void ReportsNoSelectableEndpointsBecauseCaptureIsSystemWide()
    {
        Assert.Empty(new MacOsOutputDeviceProbe().GetOutputDevices());
    }
}
