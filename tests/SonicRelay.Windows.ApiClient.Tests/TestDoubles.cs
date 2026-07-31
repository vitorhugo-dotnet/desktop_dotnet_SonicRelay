using System.Net;
using SonicRelay.Windows.Core.Authentication;

namespace SonicRelay.Windows.ApiClient.Tests;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };
}

/// <summary>A fixed device-access token, for tests that don't care about token refresh.</summary>
internal sealed class StaticAccessTokenProvider(string token) : IDeviceAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(token);
}

internal static class TestClient
{
    public static HttpClient Create(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://backend.example/")
    };
}
