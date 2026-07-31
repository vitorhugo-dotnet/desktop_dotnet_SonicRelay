using SonicRelay.Platform.Linux.Storage;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Platform.Linux.Tests.Storage;

public sealed class InMemoryTokenStoreTests
{
    [Fact]
    public async Task LoadReturnsNoTokensBeforeAnySave()
    {
        var store = new InMemoryTokenStore();
        var result = await store.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTheTokens()
    {
        var store = new InMemoryTokenStore();
        var tokens = new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1));

        var saveResult = await store.SaveAsync(tokens);
        var loadResult = await store.LoadAsync();

        Assert.True(saveResult.Succeeded);
        Assert.Equal(tokens, loadResult.Tokens);
    }

    [Fact]
    public async Task DeleteClearsTheStoredTokens()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        var deleteResult = await store.DeleteAsync();
        var loadResult = await store.LoadAsync();

        Assert.True(deleteResult.Succeeded);
        Assert.Null(loadResult.Tokens);
    }
}
