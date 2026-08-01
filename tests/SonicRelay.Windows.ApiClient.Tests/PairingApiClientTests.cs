using System.Net;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class PairingApiClientTests
{
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
}
