using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SonicRelay.Windows.ApiClient.Settings;
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

public sealed class PublisherRuntimeRelaySettingsTests
{
    // CreateSessionAsync only reaches the point where PublisherWorkflow sets
    // state.SessionId (which is what triggers PublisherRuntime's session-start
    // refresh) once device-identity bootstrap/token exchange and session creation
    // have all actually succeeded against a backend — a fully unreachable host
    // never gets past PublisherWorkflow's own "device not initialized" validation
    // guard, so this spins up a minimal loopback HTTP backend that answers just
    // enough of those endpoints for the real call chain to reach session start.
    // The subsequent signaling WebSocket connect is left to fail against this
    // fake backend (it doesn't implement the signaling protocol) — that failure
    // happens after the SessionId state transition this test cares about, and
    // PublisherWorkflow already rolls the session back cleanly on that failure.
    [Fact]
    public async Task Starting_a_session_refreshes_relay_settings_from_the_backend()
    {
        await using var backend = await FakeSessionBackend.StartAsync();
        var calls = 0;
        var stub = new RecordingRelaySettingsApiClient(() => calls++);
        // Route the refreshed RelayMode to a throwaway temp file rather than the real
        // %LocalAppData%/SonicRelay/WindowsPublisher/preferences.json — a successful
        // refresh in this test really does write through RelayPreferenceStore, and
        // without this override it would clobber a real user's saved relay mode on any
        // machine that also runs the actual app under the same account (mirrors the
        // AudioOutputPreferenceStore temp-path pattern used elsewhere in this file).
        var relayPreferencePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "relay-preferences.json");
        // Snapshot the real, shared preferences file's state (if any) so we can assert
        // afterward that this test never touched it — asserting purely on the temp
        // path wouldn't catch a bug where PublisherRuntime.Create ignored the override
        // and still wrote to DefaultPath too.
        var defaultPathExistedBefore = File.Exists(RelayPreferenceStore.DefaultPath);
        var defaultPathWriteTimeBefore = defaultPathExistedBefore ? File.GetLastWriteTimeUtc(RelayPreferenceStore.DefaultPath) : default;
        try
        {
            var relayPreference = new RelayPreferenceStore(relayPreferencePath);
            await using var runtime = PublisherRuntime.Create(
                backend.BaseUrl,
                new FakeAudio(),
                credentialStoreOverride: new StatefulFakeDeviceCredentialStore(),
                relaySettingsApiOverride: stub,
                relayPreferenceOverride: relayPreference);

            await runtime.InitializeDeviceIdentityAsync();
            // Signaling connect against the fake backend fails once the session has
            // already been created and its id recorded (see the type-level comment
            // above); that failure is expected and irrelevant to this assertion.
            // PublisherWorkflow swallows the failure internally (ExecuteAsync records
            // it as State.ErrorMessage rather than throwing), so no try/catch is needed.
            await runtime.Workflow.CreateSessionAsync();

            Assert.Equal(1, calls);
            // Confirms the refresh actually persisted through the temp-path override
            // (not silently to DefaultPath) and that RelayPreference picked up the
            // fetched mode.
            Assert.True(File.Exists(relayPreferencePath));
            Assert.Equal("automatic", relayPreference.RelayMode);
            Assert.Equal(defaultPathExistedBefore, File.Exists(RelayPreferenceStore.DefaultPath));
            if (defaultPathExistedBefore)
            {
                Assert.Equal(defaultPathWriteTimeBefore, File.GetLastWriteTimeUtc(RelayPreferenceStore.DefaultPath));
            }
        }
        finally
        {
            var directory = Path.GetDirectoryName(relayPreferencePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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

    /// <summary>
    /// Unlike <see cref="PublisherRuntimeTests"/>'s own credential-store fake (which
    /// always reports "no stored credential" and exists only for construction smoke
    /// tests that never call <see cref="PublisherRuntime.InitializeDeviceIdentityAsync"/>),
    /// this one actually persists what it's given — <c>InitializeDeviceIdentityAsync</c>
    /// here needs a real load-what-was-saved round trip to reach
    /// <c>IsAuthenticated = true</c> against the fake backend below.
    /// </summary>
    private sealed class StatefulFakeDeviceCredentialStore : IDeviceCredentialStore
    {
        private DeviceCredential? stored;

        public Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            stored = credential;
            return Task.FromResult(DeviceCredentialStorageResult.Success(credential));
        }

        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(stored is null ? DeviceCredentialStorageResult.Success() : DeviceCredentialStorageResult.Success(stored));

        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
        {
            stored = null;
            return Task.FromResult(DeviceCredentialStorageResult.Success());
        }
    }

    private sealed class RecordingRelaySettingsApiClient(Action onGet) : IRelaySettingsApiClient
    {
        public Task<RelaySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            onGet();
            return Task.FromResult(new RelaySettingsResponse("automatic", [], false));
        }

        public Task<RelaySettingsResponse> UpdateAsync(UpdateRelaySettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A minimal loopback HTTP server answering only the three REST endpoints
    /// <see cref="PublisherWorkflow.InitializeDeviceIdentityAsync"/> and the start
    /// of <see cref="PublisherWorkflow.CreateSessionAsync"/> need
    /// (device bootstrap, device token, and session creation) — just enough for
    /// the real, unmodified call chain to reach the SessionId state transition
    /// under test, without a full backend.
    /// </summary>
    private sealed class FakeSessionBackend : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly Task acceptLoop;
        private readonly CancellationTokenSource stopping = new();

        private FakeSessionBackend(HttpListener listener, int port)
        {
            this.listener = listener;
            // Bind and connect via the literal hostname "localhost", not the IP-literal
            // 127.0.0.1: on Windows, HTTP.sys auto-grants a non-admin process's URL-prefix
            // reservation only for "localhost" — binding an IP-literal without elevation
            // (or a prior `netsh http add urlacl`) throws HttpListenerException: Access is
            // denied on a real Windows runner, which is this repo's primary target platform.
            BaseUrl = new Uri($"http://localhost:{port}/");
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUrl { get; }

        public static Task<FakeSessionBackend> StartAsync()
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            return Task.FromResult(new FakeSessionBackend(listener, port));
        }

        private static int GetFreeLoopbackPort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            try { return ((IPEndPoint)socket.LocalEndpoint).Port; }
            finally { socket.Stop(); }
        }

        private async Task AcceptLoopAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                _ = RespondAsync(context);
            }
        }

        private static async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                var body = context.Request.Url?.AbsolutePath switch
                {
                    "/api/devices/bootstrap" => JsonSerializer.Serialize(new
                    {
                        deviceId = Guid.NewGuid(),
                        credentialSecret = "fake-secret",
                        credentialVersion = 1
                    }),
                    "/api/devices/token" => JsonSerializer.Serialize(new
                    {
                        accessToken = "fake-token",
                        expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        scopes = Array.Empty<string>()
                    }),
                    "/api/sessions/" => JsonSerializer.Serialize(new
                    {
                        id = Guid.NewGuid(),
                        sourceDeviceId = Guid.NewGuid(),
                        status = "created",
                        maxViewers = 4,
                        codeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                        startedAt = (DateTimeOffset?)null,
                        endedAt = (DateTimeOffset?)null,
                        createdAt = DateTimeOffset.UtcNow,
                        code = "ABC123"
                    }),
                    _ => null
                };

                context.Response.StatusCode = body is null ? (int)HttpStatusCode.NotFound : (int)HttpStatusCode.OK;
                if (body is not null)
                {
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
            {
                // The client (or the test) may have already moved on.
            }
            finally
            {
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            listener.Stop();
            listener.Close();
            try { await acceptLoop; } catch { }
            stopping.Dispose();
        }
    }
}
