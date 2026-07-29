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
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;
    private int accessTokenGeneration;

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
        if (!forceRefresh && HasUsableCachedToken())
        {
            return accessToken!;
        }

        var observedGeneration = accessTokenGeneration;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (HasUsableCachedToken() && (!forceRefresh || accessTokenGeneration > observedGeneration))
            {
                return accessToken!;
            }

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
                await ClearCredentialAsync(cancellationToken);
                throw;
            }

            accessToken = token.AccessToken;
            accessTokenExpiresAt = token.ExpiresAt;
            accessTokenGeneration++;
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private bool HasUsableCachedToken() =>
        !string.IsNullOrWhiteSpace(accessToken)
        && accessTokenExpiresAt > timeProvider.GetUtcNow().Add(ExpiryMargin);

    private void ClearCachedToken()
    {
        accessToken = null;
        accessTokenExpiresAt = default;
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
