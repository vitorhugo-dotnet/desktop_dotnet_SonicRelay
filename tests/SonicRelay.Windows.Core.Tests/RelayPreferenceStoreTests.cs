using System.Text.Json;
using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Core.Tests;

public sealed class RelayPreferenceStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonicrelay-prefs-{Guid.NewGuid():N}.json");

    [Fact]
    public void DefaultsToAutomaticWhenNoFileExists()
    {
        var store = new RelayPreferenceStore(path);

        Assert.Equal(RelayModes.Automatic, store.RelayMode);
        Assert.False(store.ForceRelay);
    }

    [Fact]
    public async Task PersistsRelayModeAcrossInstances()
    {
        await new RelayPreferenceStore(path).SetRelayModeAsync(RelayModes.ForceRelay);

        var reloaded = new RelayPreferenceStore(path);
        Assert.Equal(RelayModes.ForceRelay, reloaded.RelayMode);
        Assert.True(reloaded.ForceRelay);
    }

    [Fact]
    public async Task ApplyFetchedRelayModePersistsWithoutRequiringAWriteThroughCaller()
    {
        await new RelayPreferenceStore(path).ApplyFetchedRelayModeAsync(RelayModes.DisableFallback);

        Assert.Equal(RelayModes.DisableFallback, new RelayPreferenceStore(path).RelayMode);
    }

    [Fact]
    public void ReadingAnOldBooleanShapedFileMigratesForceRelayTrueToTheForceRelayMode()
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { ForceRelay = true }));

        Assert.Equal(RelayModes.ForceRelay, new RelayPreferenceStore(path).RelayMode);
    }

    [Fact]
    public void ReadingAnOldBooleanShapedFileMigratesForceRelayFalseToAutomatic()
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { ForceRelay = false }));

        Assert.Equal(RelayModes.Automatic, new RelayPreferenceStore(path).RelayMode);
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
