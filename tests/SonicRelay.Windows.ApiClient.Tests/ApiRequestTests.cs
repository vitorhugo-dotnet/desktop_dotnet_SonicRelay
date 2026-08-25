using System.Net;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class ApiRequestTests
{
    [Fact]
    public async Task SessionOperationsUseDocumentedRoutesAndBodies()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body, string? Token)>();
        var sessionId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var deviceId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var response = """{"id":"00000000-0000-0000-0000-000000000003","sourceDeviceId":"00000000-0000-0000-0000-000000000002","status":"waiting","maxViewers":3,"codeExpiresAt":"2026-01-01T00:10:00Z","startedAt":null,"endedAt":null,"createdAt":"2026-01-01T00:00:00Z","code":"ABC123"}""";
        var activeResponse = """[{"id":"00000000-0000-0000-0000-000000000003","sourceDeviceId":"00000000-0000-0000-0000-000000000002","status":"waiting","maxViewers":3,"codeExpiresAt":"2026-01-01T00:10:00Z","startedAt":null,"endedAt":null,"createdAt":"2026-01-01T00:00:00Z","viewerCount":1}]""";
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.Parameter));
            return request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, activeResponse)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, response);
        });
        var client = new SessionApiClient(
            TestClient.Create(handler),
            new SequenceAccessTokenProvider("device-access"));

        var created = await client.CreateSessionAsync(new CreateSessionRequest(3));
        var active = await client.GetActiveSessionsAsync();
        await client.EndSessionAsync(sessionId);

        Assert.Equal(deviceId, created.SourceDeviceId);
        Assert.Equal(deviceId, Assert.Single(active).SourceDeviceId);
        Assert.Equal((HttpMethod.Post, "/api/sessions/", """{"maxViewers":3}""", "device-access"), requests[0]);
        Assert.Equal((HttpMethod.Get, "/api/sessions/active", null, "device-access"), requests[1]);
        Assert.Equal((HttpMethod.Post, $"/api/sessions/{sessionId}/end", null, "device-access"), requests[2]);
    }

    [Fact]
    public async Task DuplexSessionOperationsUseDocumentedRoutesAndBodies()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var sessionId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var participantId = Guid.Parse("00000000-0000-0000-0000-000000000009");
        var created = """{"id":"00000000-0000-0000-0000-000000000003","sourceDeviceId":"00000000-0000-0000-0000-000000000002","status":"waiting","maxViewers":3,"codeExpiresAt":"2026-01-01T00:10:00Z","startedAt":null,"endedAt":null,"createdAt":"2026-01-01T00:00:00Z","code":"ABC123","mode":"duplex"}""";
        var participants = """{"sessionId":"00000000-0000-0000-0000-000000000003","mode":"duplex","participants":[{"participantId":"00000000-0000-0000-0000-000000000009","role":"viewer","status":"connected","audioSendAllowed":true,"canSendAudio":true,"canReceiveAudio":true,"audioMuted":false,"joinedAt":"2026-01-01T00:00:00Z","leftAt":null,"isSelf":false}]}""";
        var permission = """{"participantId":"00000000-0000-0000-0000-000000000009","role":"viewer","status":"connected","audioSendAllowed":false,"canSendAudio":false,"canReceiveAudio":true,"audioMuted":false,"joinedAt":"2026-01-01T00:00:00Z","leftAt":null,"isSelf":false}""";
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((request.Method, path,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (path.EndsWith("/audio-permission", StringComparison.Ordinal))
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, permission);
            return path.EndsWith("/participants", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, participants)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, created);
        });
        var client = new SessionApiClient(
            TestClient.Create(handler),
            new SequenceAccessTokenProvider("device-access"));

        var session = await client.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));
        var roster = await client.GetParticipantsAsync(sessionId);
        var revoked = await client.SetAudioPermissionAsync(sessionId, participantId, canSendAudio: false);

        Assert.Equal(SessionModes.Duplex, session.Mode);
        Assert.Equal(SessionModes.Duplex, roster.Mode);
        var participant = Assert.Single(roster.Participants);
        Assert.True(participant.AudioSendAllowed);
        Assert.False(revoked.AudioSendAllowed);

        // maxViewers stays null (the backend applies its own default); only `mode` is added.
        Assert.Equal((HttpMethod.Post, "/api/sessions/", """{"maxViewers":null,"mode":"duplex"}"""), requests[0]);
        Assert.Equal((HttpMethod.Get, $"/api/sessions/{sessionId}/participants", null), requests[1]);
        Assert.Equal(
            (HttpMethod.Post, $"/api/sessions/{sessionId}/participants/{participantId}/audio-permission",
                """{"canSendAudio":false}"""),
            requests[2]);
    }

    [Fact]
    public async Task ReplaySafeGetForcesOneTokenExchangeAndRetriesUnauthorized()
    {
        var tokens = new SequenceAccessTokenProvider("token-1", "token-2");
        var bearerTokens = new List<string?>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            bearerTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(bearerTokens.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));
        });
        var client = new SessionApiClient(TestClient.Create(handler), tokens);

        await client.GetActiveSessionsAsync();

        Assert.Equal(["token-1", "token-2"], bearerTokens);
        Assert.Equal([false, true], tokens.ForceRefreshCalls);
    }

    [Fact]
    public async Task SideEffectPostDoesNotReplayUnauthorized()
    {
        var tokens = new SequenceAccessTokenProvider("token-1", "token-2");
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        var client = new SessionApiClient(TestClient.Create(handler), tokens);

        var error = await Assert.ThrowsAsync<Errors.ApiClientException>(() =>
            client.CreateSessionAsync(new CreateSessionRequest(3)));

        Assert.Equal(Errors.ApiErrorKind.Unauthorized, error.Kind);
        Assert.Equal(1, requestCount);
        Assert.Equal([false], tokens.ForceRefreshCalls);
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
