using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.ApiClient.Authentication;

public sealed class AuthApiClient(HttpClient httpClient, ITokenStore tokenStore) : IAuthApiClient
{
    private readonly ApiHttpClient _api = new(httpClient, tokenStore);

    public async Task<TokenSet> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _api.SendAsync<IdentityTokenResponse>(
            HttpMethod.Post,
            "/auth/login?useCookies=false",
            request,
            authenticated: false,
            cancellationToken,
            replaySafe: false);
        var tokens = response.ToTokenSet();
        await _api.SaveTokensAsync(tokens, cancellationToken);
        return tokens;
    }

    public Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        // ASP.NET Core Identity's /register returns success with no body and no tokens,
        // so this sends an unauthenticated request and never persists tokens.
        _api.SendAsync(
            HttpMethod.Post,
            "/auth/register",
            request,
            authenticated: false,
            cancellationToken,
            replaySafe: false);

    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _api.RefreshTokensAsync(refreshToken, cancellationToken);

    public Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        _api.SendAsync<CurrentUserResponse>(
            HttpMethod.Get,
            "/auth/me",
            null,
            true,
            cancellationToken,
            replaySafe: true);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        // Identity issues stateless bearer tokens, so signing out is a local token wipe.
        _api.ClearTokensAsync(cancellationToken);

    public async Task DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        // Soft-deletes the account server-side (revokes devices/sessions, blocks login,
        // emails an operator via n8n), then wipes local credentials.
        await _api.SendAsync(HttpMethod.Delete, "/api/account", null, authenticated: true, cancellationToken);
        await _api.ClearTokensAsync(cancellationToken);
    }
}
