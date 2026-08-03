using Avalonia.Headless.XUnit;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Desktop.Controls;
using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Reproduces a production crash: the publisher desktop app terminated with a
/// <see cref="NullReferenceException"/> in <c>PairingView.Render()</c> as soon as device-identity
/// bootstrap attached a real <see cref="PairingViewModel"/>, because <c>Attach</c> rendered
/// synchronously before the control had ever loaded (named XAML elements not guaranteed to exist).
/// </summary>
public sealed class PairingViewLifecycleTests
{
    [AvaloniaFact]
    public void Attaching_a_pairing_view_model_before_load_does_not_throw()
    {
        var view = new PairingView();
        var viewModel = new PairingViewModel(new StubPairingApiClient(), new StubQrCodeService(), Guid.NewGuid());

        // Never shown/added to a window, so PairingView.Loaded never fires — this is exactly
        // what happens when the publisher runtime finishes device-identity bootstrap and attaches
        // a real PairingViewModel before the shell's first layout pass completes.
        var exception = Record.Exception(() => view.DataContext = viewModel);

        Assert.Null(exception);
    }

    private sealed class StubPairingApiClient : IPairingApiClient
    {
        public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CreatePairingChallengeResponse(Guid.NewGuid(), "PAIR01", "opaque", DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PairingResponse>>([]);

        public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubQrCodeService : IPairingQrCodeService
    {
        public byte[] RenderPng(string payload) => [0x89, 0x50, 0x4E, 0x47];
    }
}
