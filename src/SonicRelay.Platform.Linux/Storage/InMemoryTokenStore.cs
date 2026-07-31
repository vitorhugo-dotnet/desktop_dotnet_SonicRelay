using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Storage;

/// <summary>
/// Session-only token storage for when Secret Service is unavailable. Never
/// creates a plaintext file (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md,
/// "Linux token storage" — fallback). Tokens are lost on process exit, so the
/// user must sign in again after restarting SonicRelay.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private TokenSet? tokens;

    public Task<TokenStorageResult> SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        this.tokens = tokens;
        return Task.FromResult(TokenStorageResult.Success());
    }

    public Task<TokenStorageResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TokenStorageResult.Success(tokens));

    public Task<TokenStorageResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        tokens = null;
        return Task.FromResult(TokenStorageResult.Success());
    }
}
