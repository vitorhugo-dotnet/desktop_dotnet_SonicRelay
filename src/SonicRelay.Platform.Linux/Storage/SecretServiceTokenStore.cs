using System.Text.Json;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Storage;

/// <summary>
/// Persists tokens via `secret-tool` (Secret Service). Fixed attributes identify
/// the entry; the secret is always provided on stdin, never as an argument or
/// logged. Unavailable/locked Secret Service maps to SecureStorageUnavailable so
/// the caller can fall back to session-only storage (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
/// "Linux token storage" — ADR-LINUX-007).
/// </summary>
public sealed class SecretServiceTokenStore(ILinuxProcessRunner processRunner, string secretToolPath) : ITokenStore
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Attributes = ["application", "sonicrelay", "purpose", "publisher-token"];

    public async Task<TokenStorageResult> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string[] arguments = ["store", "--label=SonicRelay publisher token", .. Attributes];
        var payload = JsonSerializer.Serialize(tokens);
        var result = await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken, standardInput: payload).ConfigureAwait(false);
        return result.ExitCode == 0
            ? TokenStorageResult.Success()
            : TokenStorageResult.SecureStorageUnavailable("Secret Service is unavailable or locked.");
    }

    public async Task<TokenStorageResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        string[] arguments = ["lookup", .. Attributes];
        var result = await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        // A non-zero exit here means "no matching secret", not a broken Secret
        // Service — that is simply "no stored session", the same as a missing
        // token file on Windows.
        if (result.ExitCode != 0) return TokenStorageResult.Success();

        try
        {
            var tokens = JsonSerializer.Deserialize<TokenSet>(result.StandardOutput.TrimEnd('\n'));
            return tokens is null
                ? TokenStorageResult.Failed("Stored token data is invalid.")
                : TokenStorageResult.Success(tokens);
        }
        catch (JsonException)
        {
            return TokenStorageResult.Failed("Stored token data is invalid.");
        }
    }

    public async Task<TokenStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        string[] arguments = ["clear", .. Attributes];
        // secret-tool clear exits non-zero when nothing was stored; that is not
        // a failure from the caller's point of view (there is nothing to delete).
        await processRunner.RunAsync(secretToolPath, arguments, CommandTimeout, cancellationToken).ConfigureAwait(false);
        return TokenStorageResult.Success();
    }
}
