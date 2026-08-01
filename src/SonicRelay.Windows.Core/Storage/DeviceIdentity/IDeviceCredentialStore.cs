namespace SonicRelay.Windows.Core.Storage.DeviceIdentity;

public interface IDeviceCredentialStore
{
    Task<DeviceCredentialStorageResult> SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default);
    Task<DeviceCredentialStorageResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<DeviceCredentialStorageResult> DeleteAsync(CancellationToken cancellationToken = default);
}
