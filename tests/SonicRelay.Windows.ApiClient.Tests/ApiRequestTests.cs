using System.Net;
using SonicRelay.Windows.ApiClient.Authentication;
using SonicRelay.Windows.ApiClient.Devices;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class ApiRequestTests
{
    [Fact]
    public async Task LoginUsesIdentityRouteAndCamelCaseBody()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"tokenType":"Bearer","accessToken":"access","expiresIn":900,"refreshToken":"refresh"}""");
        });
        var store = new MemoryTokenStore();

        var tokens = await new AuthApiClient(TestClient.Create(handler), store)
            .LoginAsync(new LoginRequest("user@example.com", "secret"));

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/auth/login?useCookies=false", captured.RequestUri!.PathAndQuery);
        Assert.Equal("""{"email":"user@example.com","password":"secret"}""", body);
        Assert.Equal("access", tokens.AccessToken);
        Assert.Equal(tokens, store.Tokens);
    }

    [Fact]
    public async Task RegisterPostsToIdentityRouteWithCamelCaseBodyAndNoBearer()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var store = new MemoryTokenStore();

        await new AuthApiClient(TestClient.Create(handler), store)
            .RegisterAsync(new RegisterRequest("new@example.com", "secret"));

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/auth/register", captured.RequestUri!.AbsolutePath);
        Assert.Null(captured.Headers.Authorization);
        Assert.Equal("""{"email":"new@example.com","password":"secret"}""", body);
        Assert.Null(store.Tokens);
    }

    [Fact]
    public async Task RegisterSurfacesIdentityValidationErrors()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"DuplicateUserName":["Email 'new@example.com' is already taken."]}}""")));

        var error = await Assert.ThrowsAsync<Errors.ApiClientException>(() =>
            new AuthApiClient(TestClient.Create(handler), new MemoryTokenStore())
                .RegisterAsync(new RegisterRequest("new@example.com", "secret")));

        Assert.Equal(Errors.ApiErrorKind.Validation, error.Kind);
        Assert.Contains("already taken", error.Message);
    }

    [Fact]
    public async Task CurrentUserUsesBearerToken()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"00000000-0000-0000-0000-000000000001","email":"u@example.com","displayName":"User","emailConfirmed":true,"createdAt":"2026-01-01T00:00:00Z","lastLoginAt":null}"""));
        });
        var store = new MemoryTokenStore(new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5)));

        await new AuthApiClient(TestClient.Create(handler), store).GetCurrentUserAsync();

        Assert.Equal("/auth/me", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("access", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task RegisterDeviceUsesPublisherShape()
    {
        string? body = null;
        string? path = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":"00000000-0000-0000-0000-000000000002","name":"Desktop","type":"windows_publisher","platform":"windows","publicKey":null,"trusted":false,"revoked":false,"lastSeenAt":null,"createdAt":"2026-01-01T00:00:00Z"}""");
        });
        var client = new DeviceApiClient(TestClient.Create(handler), ValidStore());

        await client.RegisterWindowsPublisherAsync(new RegisterDeviceRequest("Desktop", null));

        Assert.Equal("/api/devices/", path);
        Assert.Equal("""{"name":"Desktop","type":"windows_publisher","platform":"windows","publicKey":null}""", body);
    }

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

    [Fact]
    public async Task LegacySideEffectPostDoesNotReplayUnauthorized()
    {
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        var client = new DeviceApiClient(TestClient.Create(handler), ValidStore());

        var error = await Assert.ThrowsAsync<Errors.ApiClientException>(() =>
            client.RegisterWindowsPublisherAsync(new RegisterDeviceRequest("Desktop", null)));

        Assert.Equal(Errors.ApiErrorKind.Unauthorized, error.Kind);
        Assert.Equal(1, requestCount);
    }

    private static MemoryTokenStore ValidStore() =>
        new(new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5)));

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
