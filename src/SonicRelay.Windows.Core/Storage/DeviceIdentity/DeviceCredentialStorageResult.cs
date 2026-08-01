namespace SonicRelay.Windows.Core.Storage.DeviceIdentity;

public enum DeviceCredentialStorageStatus
{
    Success,
    SecureStorageUnavailable,
    Failed
}

public sealed record DeviceCredentialStorageResult(
    DeviceCredentialStorageStatus Status,
    DeviceCredential? Credential = null,
    string? Message = null)
{
    public bool Succeeded => Status == DeviceCredentialStorageStatus.Success;

    public static DeviceCredentialStorageResult Success(DeviceCredential? credential = null) => new(DeviceCredentialStorageStatus.Success, credential);
    public static DeviceCredentialStorageResult SecureStorageUnavailable(string message) => new(DeviceCredentialStorageStatus.SecureStorageUnavailable, null, message);
    public static DeviceCredentialStorageResult Failed(string message) => new(DeviceCredentialStorageStatus.Failed, null, message);
}
