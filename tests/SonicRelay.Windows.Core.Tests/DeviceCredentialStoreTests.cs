using System.Text;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;

namespace SonicRelay.Windows.Core.Tests;

public sealed class DeviceCredentialStoreTests : IDisposable
{
    private static readonly Guid DeviceId = Guid.Parse("8ab2f8dc-9ff5-4ee7-91b9-85e9c3cda2d0");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SonicRelay-device-credential-{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_load_and_delete_round_trip_one_atomic_credential()
    {
        var store = CreateStore();
        var expected = new DeviceCredential(DeviceId, "secret", 1, "windows_publisher", "windows");

        Assert.True((await store.SaveAsync(expected)).Succeeded);
        Assert.Equal(expected, (await store.LoadAsync()).Credential);
        Assert.True((await store.DeleteAsync()).Succeeded);
        Assert.Null((await store.LoadAsync()).Credential);
    }

    [Fact]
    public async Task Failed_replacement_leaves_the_existing_credential_readable()
    {
        // A shared-read handle only blocks a same-process rename over the target on
        // Windows' mandatory locking; POSIX rename() happily replaces an open file, so
        // this scenario cannot be exercised on Linux/macOS (matches the existing
        // OperatingSystem-guard convention used for other platform-specific tests, e.g.
        // LinuxProcessRunnerTests).
        if (!OperatingSystem.IsWindows()) return;

        var store = CreateStore();
        var existing = new DeviceCredential(DeviceId, "old-secret", 1, "windows_publisher", "windows");
        var replacement = existing with { CredentialSecret = "new-secret", CredentialVersion = 2 };
        Assert.True((await store.SaveAsync(existing)).Succeeded);

        await using var lockHandle = new FileStream(
            Path.Combine(_directory, "device-credential.dat"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        Assert.False((await store.SaveAsync(replacement)).Succeeded);
        Assert.Equal(existing, (await store.LoadAsync()).Credential);
    }

    [Fact]
    public async Task Failed_temporary_write_leaves_the_existing_credential_readable()
    {
        var store = CreateStore();
        var existing = new DeviceCredential(DeviceId, "old-secret", 1, "windows_publisher", "windows");
        var replacement = existing with { CredentialSecret = "new-secret", CredentialVersion = 2 };
        Assert.True((await store.SaveAsync(existing)).Succeeded);

        await using var lockHandle = new FileStream(
            Path.Combine(_directory, "device-credential.dat.tmp"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        Assert.False((await store.SaveAsync(replacement)).Succeeded);
        Assert.Equal(existing, (await store.LoadAsync()).Credential);
    }

    [Fact]
    public async Task Storage_failures_never_include_the_credential_secret()
    {
        var store = new UserScopedDeviceCredentialStore(_directory, new FailingProtector());
        var credential = new DeviceCredential(DeviceId, "sensitive-secret", 1, "windows_publisher", "windows");

        var result = await store.SaveAsync(credential);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(credential.CredentialSecret, result.Message ?? string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private UserScopedDeviceCredentialStore CreateStore() => new(_directory, new PrefixProtector());

    private sealed class PrefixProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => Encoding.UTF8.GetBytes("protected:" + Convert.ToBase64String(plaintext));
        public byte[] Unprotect(byte[] ciphertext) => Convert.FromBase64String(Encoding.UTF8.GetString(ciphertext)[10..]);
    }

    private sealed class FailingProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => throw new SecureStorageUnavailableException("DPAPI unavailable");
        public byte[] Unprotect(byte[] ciphertext) => throw new SecureStorageUnavailableException("DPAPI unavailable");
    }
}
