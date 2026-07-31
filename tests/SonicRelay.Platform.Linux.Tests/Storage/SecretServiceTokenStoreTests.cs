using System.Text.Json;
using SonicRelay.Platform.Linux.Audio;
using SonicRelay.Platform.Linux.Storage;
using SonicRelay.Platform.Linux.Tests.Fakes;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Tests.Storage;

public sealed class SecretServiceTokenStoreTests
{
    private static readonly TokenSet SampleTokens = new("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task SaveWritesTheSerializedTokensToStandardInputNeverToArguments()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, string.Empty, string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.SaveAsync(SampleTokens);

        Assert.True(result.Succeeded);
        Assert.Single(runner.RunCalls);
        var call = runner.RunCalls[0];
        Assert.Equal("secret-tool", call.Executable);
        Assert.Equal("store", call.Arguments[0]);
        Assert.DoesNotContain(call.Arguments, arg => arg.Contains(SampleTokens.AccessToken, StringComparison.Ordinal));
        Assert.DoesNotContain(call.Arguments, arg => arg.Contains(SampleTokens.RefreshToken, StringComparison.Ordinal));
        Assert.NotNull(call.StandardInput);
        var roundTripped = JsonSerializer.Deserialize<TokenSet>(call.StandardInput!);
        Assert.Equal(SampleTokens, roundTripped);
    }

    [Fact]
    public async Task SaveMapsANonZeroExitCodeToSecureStorageUnavailable()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "keyring locked"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.SaveAsync(SampleTokens);

        Assert.Equal(TokenStorageStatus.SecureStorageUnavailable, result.Status);
    }

    [Fact]
    public async Task LoadReturnsTheStoredTokensOnSuccess()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, JsonSerializer.Serialize(SampleTokens), string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(SampleTokens, result.Tokens);
    }

    [Fact]
    public async Task LoadTreatsANonZeroExitCodeAsNoStoredSessionNotAFailure()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "no matching secret"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task LoadMapsMalformedStoredDataToFailed()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, "not json", string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.LoadAsync();

        Assert.Equal(TokenStorageStatus.Failed, result.Status);
    }

    [Fact]
    public async Task DeleteSucceedsEvenWhenNothingWasStored()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(1, string.Empty, "no matching secret"));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        var result = await store.DeleteAsync();

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task NeverLogsOrArgumentsTheSecretToolAttributesWithTokenContent()
    {
        var runner = new FakeLinuxProcessRunner();
        runner.Script("secret-tool", new LinuxProcessResult(0, string.Empty, string.Empty));
        var store = new SecretServiceTokenStore(runner, "secret-tool");

        await store.SaveAsync(SampleTokens);
        await store.LoadAsync();
        await store.DeleteAsync();

        foreach (var call in runner.RunCalls)
        {
            Assert.All(call.Arguments, arg => Assert.DoesNotContain(SampleTokens.AccessToken, arg, StringComparison.Ordinal));
        }
    }
}
