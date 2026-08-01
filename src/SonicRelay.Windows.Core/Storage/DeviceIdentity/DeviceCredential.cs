namespace SonicRelay.Windows.Core.Storage.DeviceIdentity;

public sealed record DeviceCredential(
    Guid DeviceId,
    string CredentialSecret,
    int CredentialVersion,
    string DeviceType,
    string Platform);
