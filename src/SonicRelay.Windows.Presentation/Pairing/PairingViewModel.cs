using SonicRelay.Windows.ApiClient.Pairing;

namespace SonicRelay.Windows.Presentation.Pairing;

public sealed record PairingChallengeState(
    Guid ChallengeId,
    string Code,
    string QrPayload,
    DateTimeOffset ExpiresAt);

public sealed class PairingViewModel(
    IPairingApiClient pairings,
    IPairingQrCodeService qrCodes,
    Guid deviceId)
{
    public PairingChallengeState? Challenge { get; private set; }
    public byte[]? QrCodePng { get; private set; }
    public IReadOnlyList<PairingResponse> Pairings { get; private set; } = [];
    public string? SessionCode { get; private set; }
    public bool IsBusy { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event Action? StateChanged;

    public void SetSessionCode(string? sessionCode)
    {
        SessionCode = sessionCode;
        NotifyStateChanged();
    }

    public async Task RefreshChallengeAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var response = await pairings.CreatePairingChallengeAsync(cancellationToken);
            var png = qrCodes.RenderPng(response.QrPayload);
            Challenge = new PairingChallengeState(
                response.ChallengeId,
                response.Code,
                response.QrPayload,
                response.ExpiresAt);
            QrCodePng = png;
        });
    }

    public async Task RefreshPairingsAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () => Pairings = await pairings.ListPairingsAsync(deviceId, cancellationToken));
    }

    public async Task RevokePairingAsync(
        Guid pairingId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await pairings.RevokePairingAsync(pairingId, cancellationToken);
            Pairings = await pairings.ListPairingsAsync(deviceId, cancellationToken);
        });
    }

    public void ClearExpiredChallenge(DateTimeOffset now)
    {
        if (Challenge is null || Challenge.ExpiresAt > now)
        {
            return;
        }

        Challenge = null;
        QrCodePng = null;
        NotifyStateChanged();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        ErrorMessage = null;
        NotifyStateChanged();
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
