using System.Net;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class PairingApiClientTests
{
    [Fact]
    public async Task Create_challenge_refreshes_the_device_token_and_retries_after_unauthorized()
    {
        var tokens = new SequenceAccessTokenProvider("expired-token", "fresh-token");
        var bearerTokens = new List<string?>();
        var challengeId = Guid.Parse("00000000-0000-0000-0000-000000000105");
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            bearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(bearerTokens.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : FakeHttpMessageHandler.Json(
                    HttpStatusCode.Created,
                    $$"""{"challengeId":"{{challengeId}}","code":"PAIR42","qrPayload":"opaque-payload","expiresAt":"2026-07-29T12:10:00Z"}"""));
        });
        var client = new PairingApiClient(TestClient.Create(handler), tokens);

        var challenge = await client.CreatePairingChallengeAsync();

        Assert.Equal(challengeId, challenge.ChallengeId);
        Assert.Equal(["expired-token", "fresh-token"], bearerTokens);
        Assert.Equal([false, true], tokens.ForceRefreshCalls);
    }

    [Fact]
    public async Task Listing_pairings_retries_against_the_rotated_device_identity()
    {
        var oldDeviceId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var rotatedDeviceId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var provider = new RotatingDeviceIdentityProvider(oldDeviceId, rotatedDeviceId);
        var requestedPaths = new List<string>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(requestedPaths.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));
        });
        var client = new PairingApiClient(
            TestClient.Create(handler),
            provider,
            () => provider.DeviceId);

        await client.ListPairingsAsync(oldDeviceId);

        Assert.Equal(
        [
            $"/api/devices/{oldDeviceId:D}/pairings",
            $"/api/devices/{rotatedDeviceId:D}/pairings"
        ], requestedPaths);
    }

    [Fact]
    public async Task Pairing_operations_use_device_bearer_and_documented_routes()
    {
        var deviceId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var pairingId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var challengeId = Guid.Parse("00000000-0000-0000-0000-000000000103");
        var requests = new List<(HttpMethod Method, string Path, string? Token)>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter));
            return Task.FromResult(request.Method switch
            {
                { } method when method == HttpMethod.Post => FakeHttpMessageHandler.Json(
                    HttpStatusCode.Created,
                    $$"""{"challengeId":"{{challengeId}}","code":"PAIR42","qrPayload":"opaque-payload","expiresAt":"2026-07-29T12:10:00Z"}"""),
                { } method when method == HttpMethod.Get => FakeHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    $$"""[{"pairingId":"{{pairingId}}","publisherDeviceId":"{{deviceId}}","viewerDeviceId":"00000000-0000-0000-0000-000000000104","status":"active","createdAt":"2026-07-29T12:00:00Z","lastUsedAt":null}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            });
        });
        var client = new PairingApiClient(TestClient.Create(handler), new StaticDeviceTokenProvider());

        var challenge = await client.CreatePairingChallengeAsync();
        var pairings = await client.ListPairingsAsync(deviceId);
        await client.RevokePairingAsync(pairingId);

        Assert.Equal(challengeId, challenge.ChallengeId);
        Assert.Equal("opaque-payload", challenge.QrPayload);
        Assert.Equal(pairingId, Assert.Single(pairings).PairingId);
        Assert.Equal((HttpMethod.Post, "/api/pairings/challenges", "device-access"), requests[0]);
        Assert.Equal((HttpMethod.Get, $"/api/devices/{deviceId:D}/pairings", "device-access"), requests[1]);
        Assert.Equal((HttpMethod.Delete, $"/api/pairings/{pairingId:D}", "device-access"), requests[2]);
    }

    private sealed class StaticDeviceTokenProvider : IDeviceAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) => Task.FromResult("device-access");
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

    private sealed class RotatingDeviceIdentityProvider(Guid initialDeviceId, Guid rotatedDeviceId)
        : IDeviceAccessTokenProvider
    {
        public Guid DeviceId { get; private set; } = initialDeviceId;

        public Task<string> GetAccessTokenAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (forceRefresh) DeviceId = rotatedDeviceId;
            return Task.FromResult(forceRefresh ? "rotated-token" : "expired-token");
        }
    }
}
