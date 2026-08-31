using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient;

internal sealed class ApiHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly IDeviceAccessTokenProvider deviceAccessTokenProvider;

    public ApiHttpClient(HttpClient httpClient, IDeviceAccessTokenProvider deviceAccessTokenProvider)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceAccessTokenProvider = deviceAccessTokenProvider
            ?? throw new ArgumentNullException(nameof(deviceAccessTokenProvider));
    }

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken,
        bool replaySafe = false)
        => await SendAsync<TResponse>(method, () => path, body, authenticated, cancellationToken, replaySafe);

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        Func<string> path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken,
        bool replaySafe = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        var authentication = await ResolveAuthenticationAsync(authenticated, cancellationToken);
        using var response = await SendOnceAsync(method, path(), body, authentication.AccessToken, cancellationToken);

        var replay = await ResolveReplayAsync(
            response.StatusCode,
            authenticated,
            replaySafe,
            cancellationToken);
        if (replay.ShouldReplay)
        {
            using var retry = await SendOnceAsync(method, path(), body, replay.AccessToken, cancellationToken);
            return await ReadAsync<TResponse>(retry, cancellationToken);
        }

        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    public async Task SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken,
        bool replaySafe = false)
    {
        var authentication = await ResolveAuthenticationAsync(authenticated, cancellationToken);
        using var response = await SendOnceAsync(method, path, body, authentication.AccessToken, cancellationToken);

        var replay = await ResolveReplayAsync(
            response.StatusCode,
            authenticated,
            replaySafe,
            cancellationToken);
        if (replay.ShouldReplay)
        {
            using var retry = await SendOnceAsync(method, path, body, replay.AccessToken, cancellationToken);
            await EnsureSuccessAsync(retry, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        object? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiClientException(ApiErrorKind.BackendUnavailable, "The backend request timed out.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ApiClientException(ApiErrorKind.NetworkUnavailable, "The backend network is unavailable.", innerException: exception);
        }
    }

    private static async Task<TResponse> ReadAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);

        try
        {
            var value = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
            return value ?? throw new ApiClientException(ApiErrorKind.Unknown, "The backend returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new ApiClientException(ApiErrorKind.Unknown, "The backend returned an invalid JSON response.", innerException: exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }
    }

    private static async Task<ApiClientException> CreateErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var detail = await ReadErrorDetailAsync(response, cancellationToken);
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ApiErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => ApiErrorKind.Forbidden,
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ApiErrorKind.Validation,
            HttpStatusCode.Conflict => ApiErrorKind.Conflict,
            >= HttpStatusCode.InternalServerError => ApiErrorKind.BackendUnavailable,
            _ => ApiErrorKind.Unknown
        };
        return new ApiClientException(kind, detail ?? $"Backend request failed with status {(int)response.StatusCode}.", response.StatusCode);
    }

    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            foreach (var property in new[] { "error", "detail" })
            {
                if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            // ASP.NET Core Identity validation failures use ProblemDetails:
            // { "errors": { "<code>": ["<description>", ...] } }. Surface the descriptions,
            // which are already human-readable, in preference to the generic "title".
            if (TryReadProblemDetailsErrors(root, out var errors))
            {
                return errors;
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                return title.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool TryReadProblemDetailsErrors(JsonElement root, out string? message)
    {
        message = null;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var descriptions = errors.EnumerateObject()
            .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array
                ? field.Value.EnumerateArray()
                : Enumerable.Repeat(field.Value, 1))
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (descriptions.Length == 0)
        {
            return false;
        }

        message = string.Join(" ", descriptions);
        return true;
    }

    private async Task<RequestAuthentication> ResolveAuthenticationAsync(
        bool authenticated,
        CancellationToken cancellationToken)
    {
        if (!authenticated)
        {
            return default;
        }

        return new RequestAuthentication(
            await deviceAccessTokenProvider.GetAccessTokenAsync(cancellationToken: cancellationToken));
    }

    private async Task<RequestReplay> ResolveReplayAsync(
        HttpStatusCode statusCode,
        bool authenticated,
        bool replaySafe,
        CancellationToken cancellationToken)
    {
        if (statusCode != HttpStatusCode.Unauthorized || !authenticated || !replaySafe)
        {
            return default;
        }

        return new RequestReplay(
            true,
            await deviceAccessTokenProvider.GetAccessTokenAsync(
                forceRefresh: true,
                cancellationToken: cancellationToken));
    }

    private readonly record struct RequestAuthentication(string? AccessToken);
    private readonly record struct RequestReplay(bool ShouldReplay, string? AccessToken);
}
