using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.Core.Authentication;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;

namespace SonicRelay.Windows.ApiClient.DeviceIdentity;

public sealed class DeviceIdentitySession : IDeviceAccessTokenProvider, IDisposable
{
    private const string WindowsPublisherType = "windows_publisher";
    private const string WindowsPlatform = "windows";
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly IDeviceIdentityApiClient apiClient;
    private readonly IDeviceCredentialStore credentialStore;
    private readonly string deviceName;
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CachedAccessToken? cachedAccessToken;
    private Guid currentDeviceId;
    private TokenExchange? tokenExchange;
    private int identityInvalidated;
    private int disposed;

    public DeviceIdentitySession(
        IDeviceIdentityApiClient apiClient,
        IDeviceCredentialStore credentialStore,
        string? deviceName = null,
        TimeProvider? timeProvider = null)
    {
        this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        this.credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        this.deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfIdentityInvalidated();
        if (!forceRefresh && GetUsableCachedToken() is { } cachedToken)
        {
            return cachedToken.Value;
        }

        Task<string> exchange;
        lock (sync)
        {
            ThrowIfDisposed();
            ThrowIfIdentityInvalidated();
            if (!forceRefresh && GetUsableCachedToken() is { } synchronizedCachedToken)
            {
                return synchronizedCachedToken.Value;
            }

            exchange = tokenExchange?.Completion ?? StartTokenExchange();
        }

        return await exchange.WaitAsync(cancellationToken);
    }

    /// <summary>The device identity associated with the most recently exchanged access token.</summary>
    public Guid CurrentDeviceId
    {
        get
        {
            lock (sync)
            {
                if (currentDeviceId == Guid.Empty)
                    throw new InvalidOperationException("The device identity has not been initialized.");
                return currentDeviceId;
            }
        }
    }

    public void Dispose()
    {
        Task<string>? activeExchange;
        lock (sync)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            activeExchange = tokenExchange?.Completion;
        }

        lifetimeCancellation.Cancel();
        if (activeExchange is null)
        {
            lifetimeCancellation.Dispose();
            return;
        }

        _ = activeExchange.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            lifetimeCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Forgets this device's identity: clears the cached token, deletes the persisted
    /// credential and lifts the invalidation latch a prior rejected credential set
    /// (<see cref="ThrowIfIdentityInvalidated"/>), which otherwise sticks for the
    /// lifetime of this instance. Without this, a rejected/stale device credential
    /// leaves the publisher unable to bootstrap or pair again until the app restarts.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ClearCachedToken();
        var delete = await credentialStore.DeleteAsync(cancellationToken);
        if (!delete.Succeeded)
        {
            throw StorageFailure("cleared");
        }

        Volatile.Write(ref identityInvalidated, 0);
    }

    public bool IsTransientFailure(Exception exception) =>
        exception is ApiClientException
        {
            Kind: ApiErrorKind.NetworkUnavailable or ApiErrorKind.BackendUnavailable
        };

    private CachedAccessToken? GetUsableCachedToken()
    {
        var snapshot = Volatile.Read(ref cachedAccessToken);
        return snapshot is not null
            && !string.IsNullOrWhiteSpace(snapshot.Value)
            && snapshot.ExpiresAt > timeProvider.GetUtcNow().Add(ExpiryMargin)
                ? snapshot
                : null;
    }

    private void ClearCachedToken() => Volatile.Write(ref cachedAccessToken, null);

    private Task<string> StartTokenExchange()
    {
        var exchange = new TokenExchange();
        tokenExchange = exchange;
        exchange.Completion = ExchangeTokenAsync(exchange, lifetimeCancellation.Token);
        return exchange.Completion;
    }

    private async Task<string> ExchangeTokenAsync(TokenExchange exchange, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await LoadOrBootstrapCredentialAsync(cancellationToken);
            DeviceTokenResponse token;
            try
            {
                token = await apiClient.TokenAsync(
                    new DeviceTokenRequest(credential.DeviceId, credential.CredentialSecret),
                    cancellationToken);
            }
            catch (ApiClientException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
            {
                ClearCachedToken();
                Volatile.Write(ref identityInvalidated, 1);
                await ClearCredentialAsync(CancellationToken.None);
                throw;
            }

            if (!string.IsNullOrWhiteSpace(token.RotatedCredentialSecret))
            {
                if (token.DeviceId == Guid.Empty || token.CredentialVersion <= 0)
                {
                    throw new ApiClientException(
                        ApiErrorKind.Unknown,
                        "The backend returned an invalid rotated device identity.");
                }

                var rotatedCredential = new DeviceCredential(
                    token.DeviceId,
                    token.RotatedCredentialSecret,
                    token.CredentialVersion,
                    credential.DeviceType,
                    credential.Platform);
                var save = await credentialStore.SaveAsync(rotatedCredential, cancellationToken);
                if (!save.Succeeded)
                {
                    throw StorageFailure("saved");
                }
            }

            var snapshot = new CachedAccessToken(token.AccessToken, token.ExpiresAt);
            lock (sync)
            {
                currentDeviceId = token.DeviceId;
                Volatile.Write(ref cachedAccessToken, snapshot);
            }
            return snapshot.Value;
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(tokenExchange, exchange))
                {
                    tokenExchange = null;
                }
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private void ThrowIfIdentityInvalidated()
    {
        if (Volatile.Read(ref identityInvalidated) != 0)
        {
            throw new ApiClientException(
                ApiErrorKind.Unauthorized,
                "The publisher device identity was invalidated. Restart the publisher before bootstrapping again.");
        }
    }

    private sealed record CachedAccessToken(string Value, DateTimeOffset ExpiresAt);

    private sealed class TokenExchange
    {
        public Task<string> Completion { get; set; } = null!;
    }

    private async Task<DeviceCredential> LoadOrBootstrapCredentialAsync(CancellationToken cancellationToken)
    {
        var load = await credentialStore.LoadAsync(cancellationToken);
        if (!load.Succeeded)
        {
            throw StorageFailure("loaded");
        }

        if (load.Credential is not null)
        {
            return load.Credential;
        }

        var bootstrap = await apiClient.BootstrapAsync(
            new BootstrapDeviceRequest(deviceName, WindowsPublisherType, WindowsPlatform),
            cancellationToken);
        var credential = new DeviceCredential(
            bootstrap.DeviceId,
            bootstrap.CredentialSecret,
            bootstrap.CredentialVersion,
            WindowsPublisherType,
            WindowsPlatform);
        var save = await credentialStore.SaveAsync(credential, cancellationToken);
        if (!save.Succeeded)
        {
            throw StorageFailure("saved");
        }

        return credential;
    }

    private async Task ClearCredentialAsync(CancellationToken cancellationToken)
    {
        var delete = await credentialStore.DeleteAsync(cancellationToken);
        if (!delete.Succeeded)
        {
            throw StorageFailure("cleared");
        }
    }

    private static ApiClientException StorageFailure(string operation) =>
        new(ApiErrorKind.Unknown, $"The device credential could not be {operation}.");
}
