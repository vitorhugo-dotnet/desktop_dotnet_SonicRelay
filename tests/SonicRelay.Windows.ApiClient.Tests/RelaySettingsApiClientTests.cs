using System.Net;
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class RelaySettingsApiClientTests
{
    [Fact]
    public async Task GetAsync_sends_an_authenticated_GET_and_parses_the_response()
    {
        HttpRequestMessage? sentRequest = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            sentRequest = request;
            return Task.FromResult(FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """{"relayMode":"forceRelay","turnUris":["turn:relay.example.com:3478"],"hasCustomTurnSecret":true}"""));
        });
        var client = new RelaySettingsApiClient(TestClient.Create(handler), new SequenceAccessTokenProvider("token-1"));

        var response = await client.GetAsync();

        Assert.Equal(HttpMethod.Get, sentRequest!.Method);
        Assert.Equal("/api/settings/relay", sentRequest.RequestUri!.AbsolutePath);
        Assert.Equal("token-1", sentRequest.Headers.Authorization?.Parameter);
        Assert.Equal("forceRelay", response.RelayMode);
        Assert.Equal(["turn:relay.example.com:3478"], response.TurnUris);
        Assert.True(response.HasCustomTurnSecret);
    }

    [Fact]
    public async Task UpdateAsync_sends_a_PUT_with_the_request_body_and_parses_the_response()
    {
        HttpRequestMessage? sentRequest = null;
        string? sentBody = null;
        var handler = new FakeHttpMessageHandler(async (request, ct) =>
        {
            sentRequest = request;
            sentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                """{"relayMode":"disableFallback","turnUris":[],"hasCustomTurnSecret":false}""");
        });
        var client = new RelaySettingsApiClient(TestClient.Create(handler), new SequenceAccessTokenProvider("token-1"));

        var response = await client.UpdateAsync(new UpdateRelaySettingsRequest("disableFallback", null, null));

        Assert.Equal(HttpMethod.Put, sentRequest!.Method);
        Assert.Contains("\"relayMode\":\"disableFallback\"", sentBody);
        Assert.Equal("disableFallback", response.RelayMode);
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
