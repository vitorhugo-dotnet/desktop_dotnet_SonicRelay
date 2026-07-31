using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SonicRelay.Windows.Core.Storage;

/// <summary>
/// Encrypts/decrypts a small secret blob for at-rest storage. Extracted from the old
/// Identity-era <c>UserScopedTokenStore</c> (removed with issue #26's device-identity
/// migration) because <see cref="DeviceIdentity.UserScopedDeviceCredentialStore"/> still
/// needs the same DPAPI protection for the device credential file.
/// </summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class SecureStorageUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class WindowsDpapiTokenProtector : ITokenProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SecureStorageUnavailableException("Windows DPAPI is unavailable.");
        }

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);

            if (!succeeded)
            {
                throw new SecureStorageUnavailableException("Windows DPAPI operation failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            inputHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
