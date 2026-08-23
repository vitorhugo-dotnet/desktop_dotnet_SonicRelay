using SonicRelay.Platform.MacOs.Audio;
using SonicRelay.Windows.Audio;

namespace SonicRelay.Platform.MacOs.Tests;

public sealed class AudioTapLocatorTests
{
    private const string BundleMacOsDirectory = "/Applications/SonicRelay.app/Contents/MacOS";

    private sealed class StubProbe(params string[] existing) : IFileExistenceProbe
    {
        public bool Exists(string path) => existing.Contains(path);
    }

    private static AudioTapLocator Create(IFileExistenceProbe probe, string? overrideValue = null) =>
        new(probe, BundleMacOsDirectory, name => name == AudioTapLocator.OverrideVariable ? overrideValue : null);

    [Fact]
    public void ResolvesTheHelperNextToTheApplicationBinary()
    {
        var expected = Path.Combine(BundleMacOsDirectory, AudioTapLocator.ExecutableName);
        var locator = Create(new StubProbe(expected));

        Assert.Equal(expected, locator.Locate());
    }

    [Fact]
    public void FallsBackToTheBundleResourcesDirectory()
    {
        var expected = Path.Combine(BundleMacOsDirectory, "..", "Resources", AudioTapLocator.ExecutableName);
        var locator = Create(new StubProbe(expected));

        Assert.Equal(expected, locator.Locate());
    }

    /// <summary>
    /// The development override wins over a bundled helper so a locally built
    /// tap can be tested without reinstalling the app.
    /// </summary>
    [Fact]
    public void EnvironmentOverrideTakesPrecedenceOverTheBundledHelper()
    {
        var bundled = Path.Combine(BundleMacOsDirectory, AudioTapLocator.ExecutableName);
        var locator = Create(new StubProbe(bundled, "/tmp/built/sonicrelay-audio-tap"), "/tmp/built/sonicrelay-audio-tap");

        Assert.Equal("/tmp/built/sonicrelay-audio-tap", locator.Locate());
    }

    /// <summary>
    /// An override pointing at nothing is a developer mistake, and silently
    /// falling back to the bundled helper would hide it.
    /// </summary>
    [Fact]
    public void MissingOverrideTargetFailsInsteadOfFallingBack()
    {
        var bundled = Path.Combine(BundleMacOsDirectory, AudioTapLocator.ExecutableName);
        var locator = Create(new StubProbe(bundled), "/tmp/missing-tap");

        var error = Assert.Throws<AudioCaptureException>(() => locator.Locate());
        Assert.Equal(AudioCaptureError.PlatformFailure, error.Error);
        Assert.Contains("/tmp/missing-tap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingHelperReportsAnActionablePlatformFailure()
    {
        var locator = Create(new StubProbe());

        var error = Assert.Throws<AudioCaptureException>(() => locator.Locate());
        Assert.Equal(AudioCaptureError.PlatformFailure, error.Error);
        Assert.Contains(AudioTapLocator.ExecutableName, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper is only ever resolved from inside the signed app bundle: TCC
    /// grants Screen Recording consent to a bundle identity, so a copy found
    /// elsewhere would not carry the user's grant.
    /// </summary>
    [Fact]
    public void DoesNotResolveTheHelperFromPath()
    {
        var locator = Create(new StubProbe("/usr/local/bin/sonicrelay-audio-tap"));

        Assert.Throws<AudioCaptureException>(() => locator.Locate());
    }
}
