using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PublisherRuntimeTests
{
    private static readonly Uri BackendUrl = new("https://backend.example.test/");

    [Fact]
    public async Task CreateWithoutOverridesUsesTheDefaultWindowsDeviceCredentialStore()
    {
        // PublisherRuntime does not expose the credential store instance directly (it is
        // consumed internally by DeviceIdentitySession), so this is a construction smoke
        // test: the default composition path must not throw for the common case of no
        // override supplied — UserScopedDeviceCredentialStoreTests cover the store's own
        // DPAPI behavior in isolation.
        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio());

        Assert.NotNull(runtime);
    }

    [Fact]
    public async Task CreateWithACredentialStoreOverrideDoesNotThrow()
    {
        var credentialStore = new InMemoryFakeDeviceCredentialStore();

        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio(), credentialStoreOverride: credentialStore);

        Assert.NotNull(runtime);
    }

    [Fact]
    public async Task CreateWithAnAudioOutputPreferenceOverrideExposesTheSameInstance()
    {
        var preference = new AudioOutputPreferenceStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "audio-output.json"));

        await using var runtime = PublisherRuntime.Create(BackendUrl, new FakeAudio(), audioOutputPreferenceOverride: preference);

        Assert.Same(preference, runtime.AudioOutput);
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public AudioCaptureState State => AudioCaptureState.Stopped;
        public AudioCaptureDiagnostics Diagnostics { get; } = new(AudioCaptureState.Stopped, null, null, AudioLevelSnapshot.Silence, 0, 0);
        public string? PreferredDeviceId => null;
        public event Action<AudioCaptureState>? StateChanged;
        public event Action<AudioFrame>? FrameCaptured;
        public event Action<AudioLevelSnapshot>? LevelChanged;
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryFakeDeviceCredentialStore : IDeviceCredentialStore
    {
        public Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success(credential));
        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success());
        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success());
    }
}
