using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Desktop.Controls;
using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// Reproduces production crashes in <c>PairingView.Render()</c>: a <see cref="NullReferenceException"/>
/// that kept recurring across two fixes because the underlying cause was never that Render() ran too
/// early, but that its named-element fields were never wired at all (see
/// <see cref="Render_reflects_view_model_state_instead_of_throwing"/>).
/// </summary>
public sealed class PairingViewLifecycleTests
{
    /// <summary>
    /// The real, underlying cause of the crash: AvaloniaXamlLoader.Load(this) builds
    /// PairingView's visual tree correctly, but never wires the x:Name-generated fields
    /// (DeviceStatusText, QrImage, ...) that Render() touches directly — they stayed null
    /// forever, so every Render() call threw. Most call sites are fire-and-forget async
    /// Tasks that swallow the exception silently (masking the bug in every other test), but
    /// OnStateChanged's Dispatcher.UIThread.Post(Render) is not — that one crashes the process,
    /// matching the production stack trace exactly.
    /// </summary>
    [AvaloniaFact]
    public void Render_reflects_view_model_state_instead_of_throwing()
    {
        var viewModel = new PairingViewModel(new StubPairingApiClient(), new StubQrCodeService(), Guid.NewGuid());
        var view = new PairingView();
        var window = new Window { Content = view };

        window.Show();
        view.DataContext = viewModel;
        Dispatcher.UIThread.RunJobs();

        var statusText = view.FindControl<TextBlock>("DeviceStatusText");
        Assert.Equal("Publisher device identity ready", statusText?.Text);
    }

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

    /// <summary>
    /// Reproduces a second, still-live production crash with the identical stack trace
    /// (NullReferenceException in PairingView.Render()) even after the attach-before-load fix
    /// shipped: closing the publisher to the tray calls Window.Hide(), which unloads PairingView
    /// while a pairing refresh is still in flight. The refresh's continuation used to call
    /// Render() unconditionally on completion, racing the already-unloaded control.
    /// </summary>
    [AvaloniaFact]
    public async Task Refresh_completing_after_the_view_is_hidden_does_not_throw()
    {
        var gate = new TaskCompletionSource<CreatePairingChallengeResponse>();
        var api = new GatedPairingApiClient(gate.Task);
        var viewModel = new PairingViewModel(api, new StubQrCodeService(), Guid.NewGuid());
        var view = new PairingView { DataContext = viewModel };
        var window = new Window { Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(api.ChallengeRequested);

        // Close-to-tray: unloads PairingView while RefreshChallengeAsync is still pending.
        window.Hide();
        Dispatcher.UIThread.RunJobs();

        var exception = await Record.ExceptionAsync(async () =>
        {
            gate.SetResult(new CreatePairingChallengeResponse(Guid.NewGuid(), "PAIR01", "opaque", DateTimeOffset.UtcNow.AddMinutes(5)));
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        });

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

    private sealed class GatedPairingApiClient(Task<CreatePairingChallengeResponse> pendingChallenge) : IPairingApiClient
    {
        public bool ChallengeRequested { get; private set; }

        public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(CancellationToken cancellationToken = default)
        {
            ChallengeRequested = true;
            return pendingChallenge;
        }

        public Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PairingResponse>>([]);

        public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubQrCodeService : IPairingQrCodeService
    {
        // A real, minimal 1x1 PNG — Render() now actually decodes this via Avalonia.Skia's
        // Bitmap(Stream), so a truncated/fake payload throws downstream of the field-wiring fix.
        public byte[] RenderPng(string payload) =>
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    }
}
