namespace SonicRelay.Platform.Linux.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void ProjectBuildsAndReferencesWindowsAudio()
    {
        var format = SonicRelay.Windows.Audio.AudioSampleFormat.Pcm16;
        Assert.Equal(SonicRelay.Windows.Audio.AudioSampleFormat.Pcm16, format);
    }
}
