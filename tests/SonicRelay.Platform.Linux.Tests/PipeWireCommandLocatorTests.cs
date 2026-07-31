using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.Linux.Tests;

internal sealed class FakeExecutableLocator(Dictionary<string, string?> paths) : IExecutableLocator
{
    public string? Locate(string executableName) => paths.GetValueOrDefault(executableName);
}

public sealed class PipeWireCommandLocatorTests
{
    [Fact]
    public void LocateReturnsResolvedPathsWhenEverythingIsPresent()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["pw-record"] = "/usr/bin/pw-record",
            ["wpctl"] = "/usr/bin/wpctl",
            ["secret-tool"] = "/usr/bin/secret-tool",
        }));

        var paths = locator.Locate();

        Assert.Equal("/usr/bin/pw-dump", paths.PwDump);
        Assert.Equal("/usr/bin/pw-record", paths.PwRecord);
        Assert.Equal("/usr/bin/wpctl", paths.Wpctl);
        Assert.Equal("/usr/bin/secret-tool", paths.SecretTool);
    }

    [Fact]
    public void LocateReturnsNullSecretToolWhenAbsentButStillSucceeds()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["pw-record"] = "/usr/bin/pw-record",
            ["wpctl"] = "/usr/bin/wpctl",
        }));

        var paths = locator.Locate();

        Assert.Null(paths.SecretTool);
    }

    [Fact]
    public void LocateThrowsPlatformFailureNamingTheMissingRequiredTool()
    {
        var locator = new PipeWireCommandLocator(new FakeExecutableLocator(new()
        {
            ["pw-dump"] = "/usr/bin/pw-dump",
            ["wpctl"] = "/usr/bin/wpctl",
        }));

        var exception = Assert.Throws<AudioCaptureException>(() => locator.Locate());

        Assert.Equal(AudioCaptureError.PlatformFailure, exception.Error);
        Assert.Contains("pw-record", exception.Message);
    }
}
