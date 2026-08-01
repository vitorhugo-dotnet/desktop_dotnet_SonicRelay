using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.WebRtc;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class BackendIceServersProviderTests
{
    [Fact]
    public async Task EachBackendTurnRequestResolvesACurrentDeviceBearer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tokens = new SequenceAccessTokenProvider("token-1", "token-2");
        var bearerTokens = new List<string?>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            bearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(FakeHttpMessageHandler.Json(
                System.Net.HttpStatusCode.OK,
                """{"iceServers":[{"urls":["turn:relay:3478"],"username":"u","credential":"c"}],"iceTransportPolicy":"all","expiresAt":"1970-01-01T00:02:00Z"}"""));
        });
        var provider = new BackendIceServersProvider(
            new WebRtcApiClient(TestClient.Create(handler), tokens),
            time);

        await provider.GetIceServersAsync();
        time.Advance(TimeSpan.FromSeconds(61));
        await provider.GetIceServersAsync();

        Assert.Equal(["token-1", "token-2"], bearerTokens);
        Assert.Equal([false, false], tokens.ForceRefreshCalls);
    }

    [Fact]
    public async Task Maps_backend_response_to_ice_servers()
    {
        var api = new StubWebRtcApiClient(new IceServersResponse(
            [
                new IceServerResponse(["stun:sonicrelay-turn.hugodotnet.dev:3478"]),
                new IceServerResponse(
                    [
                        "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=udp",
                        "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=tcp",
                        "turns:sonicrelay-turn.hugodotnet.dev:5349?transport=tcp"
                    ],
                    "1751900000:user",
                    "secret==")
            ],
            "all",
            DateTimeOffset.UnixEpoch.AddSeconds(3600)));
        var provider = new BackendIceServersProvider(api);

        var servers = await provider.GetIceServersAsync();

        Assert.Equal(2, servers.Count);
        Assert.Equal("stun:sonicrelay-turn.hugodotnet.dev:3478", servers[0].Urls[0]);
        Assert.Equal(
        [
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=udp",
            "turn:sonicrelay-turn.hugodotnet.dev:3478?transport=tcp",
            "turns:sonicrelay-turn.hugodotnet.dev:5349?transport=tcp"
        ], servers[1].Urls);
        Assert.Equal("1751900000:user", servers[1].Username);
        Assert.Equal("secret==", servers[1].Credential);
    }

    [Fact]
    public async Task An_empty_backend_response_is_returned_as_is_not_replaced_with_stun_fallback()
    {
        var api = new StubWebRtcApiClient(new IceServersResponse([], "all", DateTimeOffset.UnixEpoch.AddSeconds(3600)));
        var provider = new BackendIceServersProvider(api, allowGoogleStunDevFallback: true);

        var servers = await provider.GetIceServersAsync();

        Assert.Empty(servers);
    }

    [Fact]
    public async Task Caches_until_expiry_minus_safety_margin()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var api = new StubWebRtcApiClient(new IceServersResponse(
            [new IceServerResponse(["turn:relay:3478"], "u", "c")], "all", DateTimeOffset.UnixEpoch.AddSeconds(3600)));
        var provider = new BackendIceServersProvider(api, time);

        await provider.GetIceServersAsync();
        time.Advance(TimeSpan.FromSeconds(3600 - 60 - 1));
        await provider.GetIceServersAsync();
        Assert.Equal(1, api.CallCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await provider.GetIceServersAsync();
        Assert.Equal(2, api.CallCount);
    }

    [Fact]
    public async Task In_dev_mode_falls_back_to_stun_when_backend_fails_with_no_cache()
    {
        var api = new StubWebRtcApiClient(new ApiClientException(ApiErrorKind.BackendUnavailable, "down"));
        var provider = new BackendIceServersProvider(api, allowGoogleStunDevFallback: true);

        var servers = await provider.GetIceServersAsync();

        var only = Assert.Single(servers);
        Assert.StartsWith("stun:", only.Urls[0]);
    }

    [Fact]
    public async Task In_production_mode_does_not_fall_back_to_stun_when_backend_fails_with_no_cache()
    {
        var api = new StubWebRtcApiClient(new ApiClientException(ApiErrorKind.BackendUnavailable, "down"));
        var provider = new BackendIceServersProvider(api, allowGoogleStunDevFallback: false);

        var servers = await provider.GetIceServersAsync();

        Assert.Empty(servers);
    }

    [Fact]
    public async Task Returns_last_good_cache_when_a_later_refresh_fails()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var api = new StubWebRtcApiClient(new IceServersResponse(
            [new IceServerResponse(["turn:relay:3478"], "u", "c")], "all", DateTimeOffset.UnixEpoch.AddSeconds(3600)));
        var provider = new BackendIceServersProvider(api, time);
        await provider.GetIceServersAsync();

        api.Fail(new ApiClientException(ApiErrorKind.NetworkUnavailable, "offline"));
        time.Advance(TimeSpan.FromHours(2)); // force a refresh attempt

        var servers = await provider.GetIceServersAsync();
        Assert.Equal("turn:relay:3478", servers[0].Urls[0]);
    }

    private sealed class StubWebRtcApiClient : IWebRtcApiClient
    {
        private IceServersResponse? response;
        private Exception? failure;

        public StubWebRtcApiClient(IceServersResponse response) => this.response = response;
        public StubWebRtcApiClient(Exception failure) => this.failure = failure;

        public int CallCount { get; private set; }

        public void Fail(Exception exception) { failure = exception; response = null; }

        public Task<IceServersResponse> GetIceServersAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (failure is not null) return Task.FromException<IceServersResponse>(failure);
            return Task.FromResult(response!);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private sealed class SequenceAccessTokenProvider(params string[] tokens) : IDeviceAccessTokenProvider
    {
        private readonly Queue<string> tokens = new(tokens);
        public List<bool> ForceRefreshCalls { get; } = [];

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            ForceRefreshCalls.Add(forceRefresh);
            return Task.FromResult(tokens.Count > 1 ? tokens.Dequeue() : tokens.Peek());
        }
    }
}
