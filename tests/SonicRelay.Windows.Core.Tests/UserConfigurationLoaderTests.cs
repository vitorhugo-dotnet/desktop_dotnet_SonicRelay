using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Core.Tests;

public sealed class UserConfigurationLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SonicRelay-config-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsyncReturnsValidatedConfiguration()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, """
            {"backendBaseUrl":"https://api.example.test/","signalingBaseUrl":"wss://signal.example.test/","defaultMaxViewers":4,"developmentMode":true}
            """);

        var result = await new UserConfigurationLoader(path).LoadAsync();

        Assert.Equal(new Uri("https://api.example.test/"), result.BackendBaseUrl);
        Assert.Equal(new Uri("wss://signal.example.test/"), result.SignalingBaseUrl);
        Assert.Equal(4, result.DefaultMaxViewers);
        Assert.True(result.DevelopmentMode);
    }

    [Fact]
    public async Task LoadAsyncCreatesProductionConfigurationForANewInstallation()
    {
        var path = Path.Combine(_directory, "appsettings.json");

        var result = await new UserConfigurationLoader(path).LoadAsync();

        Assert.Equal(new Uri("https://sonicrelay-api.hugodotnet.dev/"), result.BackendBaseUrl);
        Assert.Equal(new Uri("wss://sonicrelay-api.hugodotnet.dev/ws/signaling"), result.SignalingBaseUrl);
    }

    [Fact]
    public async Task LoadAsyncMigratesTheLegacyLocalhostConfigurationFromAnExistingInstallation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, """
            {"backendBaseUrl":"https://localhost:5001/","signalingBaseUrl":"wss://localhost:5001/ws/signaling","defaultMaxViewers":7,"developmentMode":false}
            """);

        var result = await new UserConfigurationLoader(path).LoadAsync();

        Assert.Equal(new Uri("https://sonicrelay-api.hugodotnet.dev/"), result.BackendBaseUrl);
        Assert.Equal(new Uri("wss://sonicrelay-api.hugodotnet.dev/ws/signaling"), result.SignalingBaseUrl);
        Assert.Equal(7, result.DefaultMaxViewers);
        Assert.False(result.DevelopmentMode);
    }

    [Theory]
    [InlineData("not-a-url", "wss://signal.example.test/", 1)]
    [InlineData("file:///tmp/api", "wss://signal.example.test/", 1)]
    [InlineData("https://api.example.test/", "relative", 1)]
    [InlineData("https://api.example.test/", "wss://signal.example.test/", 0)]
    public async Task LoadAsyncRejectsInvalidConfiguration(string backend, string signaling, int maxViewers)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, $$"""
            {"backendBaseUrl":"{{backend}}","signalingBaseUrl":"{{signaling}}","defaultMaxViewers":{{maxViewers}}}
            """);

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => new UserConfigurationLoader(path).LoadAsync());
    }

    [Fact]
    public async Task SaveBackendPersistsUrlAndDerivedSignalingForTheNextLaunch()
    {
        var path = Path.Combine(_directory, "appsettings.json");
        var loader = new UserConfigurationLoader(path);

        await loader.SaveBackendAsync(new Uri("https://api.example.test"));

        var result = await loader.LoadAsync();
        Assert.Equal(new Uri("https://api.example.test/"), result.BackendBaseUrl);
        Assert.Equal(new Uri("wss://api.example.test/ws/signaling"), result.SignalingBaseUrl);
    }

    [Fact]
    public async Task SaveBackendPreservesOtherExistingSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(path, """
            {"backendBaseUrl":"https://localhost:5001/","signalingBaseUrl":"wss://localhost:5001/ws/signaling","defaultMaxViewers":7,"developmentMode":true}
            """);
        var loader = new UserConfigurationLoader(path);

        await loader.SaveBackendAsync(new Uri("http://relay.example.test/"));

        var result = await loader.LoadAsync();
        Assert.Equal(new Uri("http://relay.example.test/"), result.BackendBaseUrl);
        Assert.Equal(new Uri("ws://relay.example.test/ws/signaling"), result.SignalingBaseUrl);
        Assert.Equal(7, result.DefaultMaxViewers);
        Assert.True(result.DevelopmentMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
