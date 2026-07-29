using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonicRelay.Windows.ApiClient.Errors;

namespace SonicRelay.Windows.ApiClient.DeviceIdentity;

public sealed class DeviceIdentityApiClient(HttpClient httpClient) : IDeviceIdentityApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<BootstrapDeviceResponse> BootstrapAsync(
        BootstrapDeviceRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<BootstrapDeviceResponse>("/api/devices/bootstrap", request, cancellationToken);

    public Task<DeviceTokenResponse> TokenAsync(
        DeviceTokenRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<DeviceTokenResponse>("/api/devices/token", request, cancellationToken);

    private async Task<TResponse> SendAsync<TResponse>(
        string path,
        object requestBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiClientException(ApiErrorKind.BackendUnavailable, "The backend request timed out.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ApiClientException(ApiErrorKind.NetworkUnavailable, "The backend network is unavailable.", innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException(
                    ErrorKind(response.StatusCode),
                    $"Backend request failed with status {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
                    ?? throw new ApiClientException(ApiErrorKind.Unknown, "The backend returned an empty response.");
            }
            catch (JsonException exception)
            {
                throw new ApiClientException(ApiErrorKind.Unknown, "The backend returned an invalid JSON response.", innerException: exception);
            }
        }
    }

    private static ApiErrorKind ErrorKind(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ApiErrorKind.Unauthorized,
        HttpStatusCode.Forbidden => ApiErrorKind.Forbidden,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ApiErrorKind.Validation,
        HttpStatusCode.Conflict => ApiErrorKind.Conflict,
        >= HttpStatusCode.InternalServerError => ApiErrorKind.BackendUnavailable,
        _ => ApiErrorKind.Unknown
    };
}
