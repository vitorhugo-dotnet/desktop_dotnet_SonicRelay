using System.Net;
using SonicRelay.Windows.ApiClient.Errors;
using SonicRelay.Windows.ApiClient.Sessions;

namespace SonicRelay.Windows.ApiClient.Tests;

/// <summary>
/// Error mapping is generic <see cref="ApiHttpClient"/> behavior shared by every device-
/// bearer API client; exercised here through <see cref="SessionApiClient"/> since the
/// Identity-era <c>DeviceApiClient</c> these tests originally used was removed with issue
/// #26's device-identity migration.
/// </summary>
public sealed class ApiErrorTests
{
    public static TheoryData<HttpStatusCode, ApiErrorKind> StatusCases => new()
    {
        { HttpStatusCode.Unauthorized, ApiErrorKind.Unauthorized },
        { HttpStatusCode.Forbidden, ApiErrorKind.Forbidden },
        { HttpStatusCode.BadRequest, ApiErrorKind.Validation },
        { HttpStatusCode.UnprocessableEntity, ApiErrorKind.Validation },
        { HttpStatusCode.Conflict, ApiErrorKind.Conflict },
        { HttpStatusCode.ServiceUnavailable, ApiErrorKind.BackendUnavailable },
        { HttpStatusCode.NotFound, ApiErrorKind.Unknown }
    };

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task MapsHttpStatus(HttpStatusCode status, ApiErrorKind expected)
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            FakeHttpMessageHandler.Json(status, """{"error":"safe message"}""")));

        var error = await Assert.ThrowsAsync<ApiClientException>(() => Client(handler).GetActiveSessionsAsync());

        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapsConnectionFailureToNetworkUnavailable()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("offline"));

        var error = await Assert.ThrowsAsync<ApiClientException>(() => Client(handler).GetActiveSessionsAsync());

        Assert.Equal(ApiErrorKind.NetworkUnavailable, error.Kind);
    }

    [Fact]
    public async Task MapsTimeoutToBackendUnavailable()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException("timeout"));

        var error = await Assert.ThrowsAsync<ApiClientException>(() => Client(handler).GetActiveSessionsAsync());

        Assert.Equal(ApiErrorKind.BackendUnavailable, error.Kind);
    }

    private static SessionApiClient Client(FakeHttpMessageHandler handler) =>
        new(TestClient.Create(handler), new StaticAccessTokenProvider("device-access"));
}
