using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PairingViewModelTests
{
    [Fact]
    public async Task Refreshing_challenge_preserves_session_code_and_renders_exact_api_payload()
    {
        var first = Challenge("00000000-0000-0000-0000-000000000201", "PAIR01", "opaque-one");
        var second = Challenge("00000000-0000-0000-0000-000000000202", "PAIR02", "opaque-two");
        var api = new FakePairingApiClient(first, second);
        var qr = new RecordingQrCodeService();
        var viewModel = new PairingViewModel(api, qr, Guid.NewGuid());
        viewModel.SetSessionCode("SESSION9");

        await viewModel.RefreshChallengeAsync();
        await viewModel.RefreshChallengeAsync();

        Assert.Equal("SESSION9", viewModel.SessionCode);
        Assert.Equal(second.ChallengeId, viewModel.Challenge!.ChallengeId);
        Assert.Equal("PAIR02", viewModel.Challenge.Code);
        Assert.Equal(["opaque-one", "opaque-two"], qr.Payloads);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], viewModel.QrCodePng);
    }

    [Fact]
    public async Task Revocation_requires_confirmation_and_refreshes_active_pairings()
    {
        var pairingId = Guid.Parse("00000000-0000-0000-0000-000000000203");
        var deviceId = Guid.Parse("00000000-0000-0000-0000-000000000204");
        var api = new FakePairingApiClient(Challenge(
            "00000000-0000-0000-0000-000000000205", "PAIR03", "opaque"));
        api.Pairings =
        [
            new PairingResponse(pairingId, deviceId, Guid.NewGuid(), "active",
                new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), null)
        ];
        var viewModel = new PairingViewModel(api, new RecordingQrCodeService(), deviceId);

        await viewModel.RefreshPairingsAsync();
        await viewModel.RevokePairingAsync(pairingId, confirmed: false);
        await viewModel.RevokePairingAsync(pairingId, confirmed: true);

        Assert.Equal([pairingId], api.RevokedPairingIds);
        Assert.Empty(viewModel.Pairings);
    }

    [Fact]
    public async Task Expired_challenge_clears_pairing_code_and_QR_without_touching_session_code()
    {
        var expiresAt = new DateTimeOffset(2026, 7, 29, 12, 5, 0, TimeSpan.Zero);
        var api = new FakePairingApiClient(new CreatePairingChallengeResponse(
            Guid.NewGuid(), "PAIR04", "opaque", expiresAt));
        var viewModel = new PairingViewModel(api, new RecordingQrCodeService(), Guid.NewGuid());
        viewModel.SetSessionCode("SESSION8");
        await viewModel.RefreshChallengeAsync();

        viewModel.ClearExpiredChallenge(expiresAt);

        Assert.Null(viewModel.Challenge);
        Assert.Null(viewModel.QrCodePng);
        Assert.Equal("SESSION8", viewModel.SessionCode);
    }

    private static CreatePairingChallengeResponse Challenge(string id, string code, string payload) =>
        new(Guid.Parse(id), code, payload, new DateTimeOffset(2026, 7, 29, 12, 10, 0, TimeSpan.Zero));

    private sealed class RecordingQrCodeService : IPairingQrCodeService
    {
        public List<string> Payloads { get; } = [];

        public byte[] RenderPng(string payload)
        {
            Payloads.Add(payload);
            return [0x89, 0x50, 0x4E, 0x47];
        }
    }

    private sealed class FakePairingApiClient(params CreatePairingChallengeResponse[] challenges)
        : IPairingApiClient
    {
        private readonly Queue<CreatePairingChallengeResponse> challenges = new(challenges);

        public IReadOnlyList<PairingResponse> Pairings { get; set; } = [];
        public List<Guid> RevokedPairingIds { get; } = [];

        public Task<CreatePairingChallengeResponse> CreatePairingChallengeAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(challenges.Dequeue());

        public Task<IReadOnlyList<PairingResponse>> ListPairingsAsync(
            Guid deviceId,
            CancellationToken cancellationToken = default) => Task.FromResult(Pairings);

        public Task RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken = default)
        {
            RevokedPairingIds.Add(pairingId);
            Pairings = Pairings.Where(pairing => pairing.PairingId != pairingId).ToArray();
            return Task.CompletedTask;
        }
    }
}
