using System.Text.Json;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.Core.Storage.DeviceIdentity;

public sealed class UserScopedDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly string _directory;
    private readonly string _path;
    private readonly ITokenProtector _protector;

    public UserScopedDeviceCredentialStore(string? directory = null, ITokenProtector? protector = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRelay",
            "WindowsPublisher");
        _path = Path.Combine(_directory, "device-credential.dat");
        _protector = protector ?? new WindowsDpapiTokenProtector();
    }

    public async Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            var protectedBytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(credential));
            Directory.CreateDirectory(_directory);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, _path, true);
            return DeviceCredentialStorageResult.Success();
        }
        catch (SecureStorageUnavailableException)
        {
            return DeviceCredentialStorageResult.SecureStorageUnavailable("Secure device credential storage is unavailable for the current user.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return DeviceCredentialStorageResult.Failed("Device credential storage operation failed.");
        }
    }

    public async Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return DeviceCredentialStorageResult.Success();

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var credential = JsonSerializer.Deserialize<DeviceCredential>(_protector.Unprotect(protectedBytes));
            return credential is null
                ? DeviceCredentialStorageResult.Failed("Stored device credential data is invalid.")
                : DeviceCredentialStorageResult.Success(credential);
        }
        catch (SecureStorageUnavailableException)
        {
            return DeviceCredentialStorageResult.SecureStorageUnavailable("Secure device credential storage is unavailable for the current user.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return DeviceCredentialStorageResult.Failed("Device credential storage operation failed.");
        }
    }

    public Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            File.Delete(_path);
            return Task.FromResult(DeviceCredentialStorageResult.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(DeviceCredentialStorageResult.Failed("Device credential deletion failed."));
        }
    }
}
