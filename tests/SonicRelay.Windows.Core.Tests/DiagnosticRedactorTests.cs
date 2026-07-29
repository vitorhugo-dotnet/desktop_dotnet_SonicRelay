using SonicRelay.Windows.Core.Diagnostics;

namespace SonicRelay.Windows.Core.Tests;

public sealed class DiagnosticRedactorTests
{
    [Fact]
    public void RedactRemovesCredentialsAndRealtimePayloads()
    {
        const string input = "password=hunter2 access_token=abc refreshToken=def bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature email=user@example.com sdp=v=0\\r\\no=- 1 2 IN IP4 127.0.0.1 candidate:1 1 UDP 1 192.168.1.2 5000 typ host";

        var result = DiagnosticRedactor.Redact(input);

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result, StringComparison.Ordinal);
        Assert.DoesNotContain("def", result, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.2", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactRemovesSensitiveUriQueryValues()
    {
        var result = DiagnosticRedactor.Redact("https://relay.test/connect?token=secret&code=private&mode=listen");

        Assert.DoesNotContain("secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private", result, StringComparison.Ordinal);
        Assert.Contains("mode=listen", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactRemovesExactDeviceAndPairingSecretAssignments()
    {
        const string input = "credentialSecret=device-secret qrPayload=qr-secret pairingCode=pair-secret sessionCode=session-secret code=short-secret statusCode=401 status-code=202 source-code=E1 codec=opus";

        var result = DiagnosticRedactor.Redact(input);

        Assert.DoesNotContain("device-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("qr-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("pair-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("short-secret", result, StringComparison.Ordinal);
        Assert.Contains("statusCode=401", result, StringComparison.Ordinal);
        Assert.Contains("status-code=202", result, StringComparison.Ordinal);
        Assert.Contains("source-code=E1", result, StringComparison.Ordinal);
        Assert.Contains("codec=opus", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("credentialSecret", true)]
    [InlineData("qrPayload", true)]
    [InlineData("pairingCode", true)]
    [InlineData("sessionCode", true)]
    [InlineData("code", true)]
    [InlineData("statusCode", false)]
    [InlineData("sourceCode", false)]
    [InlineData("codec", false)]
    public void SensitiveKeysMatchSecretSemanticsExactly(string key, bool expected)
    {
        Assert.Equal(expected, DiagnosticRedactor.IsSensitiveKey(key));
    }

    [Fact]
    public void MaskIdentifierKeepsOnlyEdges()
    {
        Assert.Equal("1234…cdef", DiagnosticRedactor.MaskIdentifier("12345678-90ab-cdef"));
        Assert.Equal("[not set]", DiagnosticRedactor.MaskIdentifier(null));
    }

    [Fact]
    public void BackendHostExcludesPathQueryAndCredentials()
    {
        var result = DiagnosticRedactor.BackendHost(new Uri("https://user:pass@relay.example:8443/private?token=secret"));

        Assert.Equal("https://relay.example:8443", result);
    }
}
