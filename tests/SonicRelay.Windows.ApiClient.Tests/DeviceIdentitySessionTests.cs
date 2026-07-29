using System.Net;
using SonicRelay.Windows.ApiClient.DeviceIdentity;
using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;

namespace SonicRelay.Windows.ApiClient.Tests;

public sealed class DeviceIdentitySessionTests
{
    private static readonly Guid DeviceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Absent_credential_bootstraps_persists_and_exchanges_for_a_token()
    {
        var api = new StubDeviceIdentityApiClient
        {
            BootstrapResponse = new BootstrapDeviceResponse(DeviceId, "secret", 2),
            TokenResponse = Token("access-1")
        };
        var store = new MemoryDeviceCredentialStore();
        var session = CreateSession(api, store);

        var token = await session.GetAccessTokenAsync();

        Assert.Equal("access-1", token);
        Assert.Equal(1, api.BootstrapCalls);
        Assert.Equal(new BootstrapDeviceRequest("Desktop", "windows_publisher", "windows"), api.BootstrapRequest);
        Assert.Equal(new DeviceCredential(DeviceId, "secret", 2, "windows_publisher", "windows"), store.Credential);
        Assert.Equal(new DeviceTokenRequest(DeviceId, "secret"), api.TokenRequest);
    }

    [Fact]
    public async Task Stored_credential_exchanges_without_bootstrapping()
    {
        var credential = new DeviceCredential(DeviceId, "stored-secret", 3, "windows_publisher", "windows");
        var api = new StubDeviceIdentityApiClient { TokenResponse = Token("access-1") };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(credential));

        var token = await session.GetAccessTokenAsync();

        Assert.Equal("access-1", token);
        Assert.Equal(0, api.BootstrapCalls);
        Assert.Equal(new DeviceTokenRequest(DeviceId, "stored-secret"), api.TokenRequest);
    }

    [Fact]
    public async Task Refreshes_when_cached_token_reaches_the_thirty_second_expiry_margin()
    {
        var time = new FakeTimeProvider(Now);
        var api = new StubDeviceIdentityApiClient
        {
            TokenResponses = new Queue<DeviceTokenResponse>(
            [
                new DeviceTokenResponse("access-1", Now.AddSeconds(31), ["session:create"]),
                new DeviceTokenResponse("access-2", Now.AddMinutes(5), ["session:create"])
            ])
        };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), time);

        var first = await session.GetAccessTokenAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var second = await session.GetAccessTokenAsync();

        Assert.Equal("access-1", first);
        Assert.Equal("access-2", second);
        Assert.Equal(2, api.TokenCalls);
    }

    [Fact]
    public async Task Does_not_cache_a_blank_access_token()
    {
        var api = new StubDeviceIdentityApiClient
        {
            TokenResponses = new Queue<DeviceTokenResponse>([Token("   "), Token("access-2")])
        };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), new FakeTimeProvider(Now));

        Assert.Equal("   ", await session.GetAccessTokenAsync());
        Assert.Equal("access-2", await session.GetAccessTokenAsync());
        Assert.Equal(2, api.TokenCalls);
    }

    [Fact]
    public async Task Force_refresh_exchanges_even_when_the_cached_token_is_still_valid()
    {
        var api = new StubDeviceIdentityApiClient
        {
            TokenResponses = new Queue<DeviceTokenResponse>([Token("access-1"), Token("access-2")])
        };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), new FakeTimeProvider(Now));

        var first = await session.GetAccessTokenAsync();
        var second = await session.GetAccessTokenAsync(forceRefresh: true);

        Assert.Equal("access-1", first);
        Assert.Equal("access-2", second);
        Assert.Equal(2, api.TokenCalls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_token_exchange()
    {
        var api = new StubDeviceIdentityApiClient { TokenResponse = Token("access-1") };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), new FakeTimeProvider(Now));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.GetAccessTokenAsync()));

        Assert.Single(tokens.Distinct());
        Assert.Equal(1, api.TokenCalls);
    }

    [Fact]
    public async Task Concurrent_forced_callers_share_one_token_exchange()
    {
        var exchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubDeviceIdentityApiClient
        {
            TokenHandler = async (_, _) =>
            {
                exchangeStarted.TrySetResult();
                await releaseExchange.Task;
                return Token("access-1");
            }
        };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), new FakeTimeProvider(Now));

        var tokensTask = Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.GetAccessTokenAsync(forceRefresh: true)));
        await exchangeStarted.Task;
        releaseExchange.SetResult();
        var tokens = await tokensTask;

        Assert.Single(tokens.Distinct());
        Assert.Equal(1, api.TokenCalls);
    }

    [Fact]
    public async Task Cancelling_the_exchange_initiator_does_not_cancel_the_shared_exchange()
    {
        var exchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubDeviceIdentityApiClient
        {
            TokenHandler = async (_, cancellationToken) =>
            {
                exchangeStarted.TrySetResult();
                await releaseExchange.Task.WaitAsync(cancellationToken);
                return Token("access-1");
            }
        };
        var session = CreateSession(api, new MemoryDeviceCredentialStore(StoredCredential()), new FakeTimeProvider(Now));
        using var initiatorCancellation = new CancellationTokenSource();

        var initiatingCaller = session.GetAccessTokenAsync(cancellationToken: initiatorCancellation.Token);
        await exchangeStarted.Task;
        var waitingCaller = session.GetAccessTokenAsync();
        initiatorCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initiatingCaller);
        releaseExchange.SetResult();

        Assert.Equal("access-1", await waitingCaller);
        Assert.Equal(1, api.TokenCalls);
    }

    [Fact]
    public async Task Concurrent_forced_callers_share_one_transport_failure()
    {
        var exchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = StoredCredential();
        var api = new StubDeviceIdentityApiClient
        {
            TokenHandler = async (_, _) =>
            {
                exchangeStarted.TrySetResult();
                await releaseExchange.Task;
                throw new ApiClientException(ApiErrorKind.NetworkUnavailable, "network unavailable");
            }
        };
        var store = new MemoryDeviceCredentialStore(credential);
        var session = CreateSession(api, store, new FakeTimeProvider(Now));

        var failuresTask = Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync(forceRefresh: true))));
        await exchangeStarted.Task;
        releaseExchange.SetResult();
        var failures = await failuresTask;

        Assert.All(failures, error => Assert.Equal(ApiErrorKind.NetworkUnavailable, error.Kind));
        Assert.Equal(1, api.TokenCalls);
        Assert.Equal(credential, store.Credential);
        Assert.Equal(0, api.BootstrapCalls);
    }

    [Fact]
    public async Task Unauthorized_token_exchange_clears_the_stored_credential()
    {
        var store = new MemoryDeviceCredentialStore(StoredCredential());
        var api = new StubDeviceIdentityApiClient
        {
            TokenException = new ApiClientException(ApiErrorKind.Unauthorized, "credential rejected", HttpStatusCode.Unauthorized)
        };
        var session = CreateSession(api, store);

        await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync());
        var invalidated = await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync());

        Assert.Equal(ApiErrorKind.Unauthorized, invalidated.Kind);
        Assert.Null(store.Credential);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(0, api.BootstrapCalls);
        Assert.Equal(1, api.TokenCalls);
    }

    [Fact]
    public async Task Unauthorized_forced_refresh_invalidates_the_session_until_it_is_recreated()
    {
        var store = new MemoryDeviceCredentialStore(StoredCredential());
        var api = new StubDeviceIdentityApiClient
        {
            BootstrapResponse = new BootstrapDeviceResponse(DeviceId, "replacement-secret", 2),
            TokenOutcomes = new Queue<object>(
            [
                Token("cached-access"),
                new ApiClientException(ApiErrorKind.Unauthorized, "credential rejected", HttpStatusCode.Unauthorized),
                Token("replacement-access")
            ])
        };
        var session = CreateSession(api, store, new FakeTimeProvider(Now));

        Assert.Equal("cached-access", await session.GetAccessTokenAsync());
        await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync(forceRefresh: true));
        var invalidated = await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync());

        Assert.Equal(ApiErrorKind.Unauthorized, invalidated.Kind);
        Assert.Equal(0, api.BootstrapCalls);
        Assert.Equal(2, api.TokenCalls);

        var recreatedSession = CreateSession(api, store, new FakeTimeProvider(Now));

        Assert.Equal("replacement-access", await recreatedSession.GetAccessTokenAsync());
        Assert.Equal(1, api.BootstrapCalls);
        Assert.Equal(3, api.TokenCalls);
    }

    [Fact]
    public async Task Network_failure_retains_the_stored_credential_without_bootstrapping()
    {
        var credential = StoredCredential();
        var store = new MemoryDeviceCredentialStore(credential);
        var api = new StubDeviceIdentityApiClient
        {
            TokenException = new ApiClientException(ApiErrorKind.NetworkUnavailable, "network unavailable")
        };
        var session = CreateSession(api, store);

        await Assert.ThrowsAsync<ApiClientException>(() => session.GetAccessTokenAsync());

        Assert.Equal(credential, store.Credential);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, api.BootstrapCalls);
    }

    [Theory]
    [InlineData(ApiErrorKind.NetworkUnavailable, true)]
    [InlineData(ApiErrorKind.BackendUnavailable, true)]
    [InlineData(ApiErrorKind.Unauthorized, false)]
    [InlineData(ApiErrorKind.Forbidden, false)]
    [InlineData(ApiErrorKind.Unknown, false)]
    public void Token_failure_classification_retries_only_transient_transport_failures(
        ApiErrorKind kind,
        bool expectedTransient)
    {
        var session = CreateSession(
            new StubDeviceIdentityApiClient(),
            new MemoryDeviceCredentialStore(StoredCredential()));

        var transient = session.IsTransientFailure(new ApiClientException(kind, "token failure"));

        Assert.Equal(expectedTransient, transient);
    }

    [Fact]
    public async Task Api_client_uses_the_backend_routes_and_json_contract()
    {
        var deviceId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var requests = new List<(string Path, string Body)>();
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return request.RequestUri.AbsolutePath.EndsWith("bootstrap", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                    $$"""{"deviceId":"{{deviceId}}","credentialSecret":"secret","credentialVersion":1}""")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"accessToken":"access-1","expiresAt":"2026-07-29T12:05:00Z","scopes":["session:create"]}""");
        });
        var client = new DeviceIdentityApiClient(TestClient.Create(handler));

        var bootstrap = await client.BootstrapAsync(new BootstrapDeviceRequest("Desktop", "windows_publisher", "windows"));
        var token = await client.TokenAsync(new DeviceTokenRequest(bootstrap.DeviceId, bootstrap.CredentialSecret));

        Assert.Equal(("/api/devices/bootstrap", """{"name":"Desktop","deviceType":"windows_publisher","platform":"windows"}"""), requests[0]);
        Assert.Equal(("/api/devices/token", $$"""{"deviceId":"{{deviceId}}","credentialSecret":"secret"}"""), requests[1]);
        Assert.Equal("access-1", token.AccessToken);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 12, 5, 0, TimeSpan.Zero), token.ExpiresAt);
        Assert.Equal(["session:create"], token.Scopes);
    }

    private static DeviceIdentitySession CreateSession(
        IDeviceIdentityApiClient api,
        IDeviceCredentialStore store,
        TimeProvider? timeProvider = null) =>
        new(api, store, "Desktop", timeProvider);

    private static DeviceCredential StoredCredential() =>
        new(DeviceId, "stored-secret", 1, "windows_publisher", "windows");

    private static DeviceTokenResponse Token(string accessToken) =>
        new(accessToken, Now.AddMinutes(5), ["session:create"]);

    private sealed class StubDeviceIdentityApiClient : IDeviceIdentityApiClient
    {
        public int BootstrapCalls { get; private set; }
        public BootstrapDeviceRequest? BootstrapRequest { get; private set; }
        public int TokenCalls { get; private set; }
        public DeviceTokenRequest? TokenRequest { get; private set; }
        public BootstrapDeviceResponse? BootstrapResponse { get; init; }
        public DeviceTokenResponse? TokenResponse { get; init; }
        public Queue<DeviceTokenResponse>? TokenResponses { get; init; }
        public Queue<object>? TokenOutcomes { get; init; }
        public Exception? TokenException { get; init; }
        public Func<DeviceTokenRequest, CancellationToken, Task<DeviceTokenResponse>>? TokenHandler { get; init; }

        public Task<BootstrapDeviceResponse> BootstrapAsync(BootstrapDeviceRequest request, CancellationToken cancellationToken = default)
        {
            BootstrapCalls++;
            BootstrapRequest = request;
            return Task.FromResult(BootstrapResponse ?? throw new InvalidOperationException("Unexpected bootstrap."));
        }

        public Task<DeviceTokenResponse> TokenAsync(DeviceTokenRequest request, CancellationToken cancellationToken = default)
        {
            TokenCalls++;
            TokenRequest = request;
            if (TokenHandler is not null) return TokenHandler(request, cancellationToken);
            if (TokenOutcomes?.Dequeue() is { } outcome)
            {
                return outcome switch
                {
                    Exception exception => Task.FromException<DeviceTokenResponse>(exception),
                    DeviceTokenResponse response => Task.FromResult(response),
                    _ => throw new InvalidOperationException("Unexpected token outcome.")
                };
            }
            if (TokenException is not null) return Task.FromException<DeviceTokenResponse>(TokenException);
            return Task.FromResult(TokenResponses?.Dequeue() ?? TokenResponse ?? throw new InvalidOperationException("Unexpected token exchange."));
        }
    }

    private sealed class MemoryDeviceCredentialStore(DeviceCredential? initial = null) : IDeviceCredentialStore
    {
        public DeviceCredential? Credential { get; private set; } = initial;
        public int DeleteCalls { get; private set; }

        public Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Credential = credential;
            return Task.FromResult(DeviceCredentialStorageResult.Success(credential));
        }

        public Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeviceCredentialStorageResult.Success(Credential));

        public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            Credential = null;
            return Task.FromResult(DeviceCredentialStorageResult.Success());
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
