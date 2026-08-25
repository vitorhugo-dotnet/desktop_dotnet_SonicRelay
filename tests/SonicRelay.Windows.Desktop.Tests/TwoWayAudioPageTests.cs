using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The two-way audio surface on the Audio page (dotnet_SonicRelay#22). Every control here is
/// driven by the backend's own published state, never by what a peer claims.
/// </summary>
public sealed class TwoWayAudioPageTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sonic-duplex-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void APageWithoutARuntimeOffersNothing()
    {
        var page = new AudioPageViewModel();

        Assert.False(page.IsConnected);
        Assert.False(page.SupportsTwoWayAudio);
        Assert.False(page.IsTwoWaySession);
        Assert.Empty(page.Participants);
    }

    [Fact]
    public void AOneWaySessionShowsNoTwoWayControls()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot { SessionId = Guid.NewGuid(), SessionMode = SessionModes.Broadcast });

        Assert.False(page.IsTwoWaySession);
        Assert.False(page.ToggleMuteCommand.CanExecute(null));
    }

    [Fact]
    public void ATwoWaySessionEnablesMuting()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot { SessionId = Guid.NewGuid(), SessionMode = SessionModes.Duplex });

        Assert.True(page.IsTwoWaySession);
        Assert.True(page.ToggleMuteCommand.CanExecute(null));
        Assert.Equal("Mute microphone", page.MuteActionLabel);
    }

    [Fact]
    public void TheMuteLabelFollowsTheSnapshot()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Duplex,
            OutgoingAudioMuted = true,
        });

        Assert.True(page.IsMuted);
        Assert.Equal("Unmute microphone", page.MuteActionLabel);
    }

    [Fact]
    public void PlaybackStatusExplainsTheDeviceRatherThanTheConnection()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot { PlaybackState = AudioPlaybackState.Faulted });

        Assert.Contains("playback device", page.PlaybackStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParticipantsSurfaceTheBackendPermissionAndAControlToChangeIt()
    {
        var page = CreatePage();
        var viewerId = Guid.NewGuid();

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Duplex,
            Participants =
            [
                new ParticipantAudioState("publisher-1", "publisher", "duplex", true, true, true, false),
                new ParticipantAudioState(viewerId.ToString("D"), "viewer", "duplex", true, true, true, false),
            ],
        });

        var publisher = page.Participants.Single(participant => participant.State.IsPublisher);
        var viewer = page.Participants.Single(participant => !participant.State.IsPublisher);

        // The publisher never revokes its own permission, so its row carries no control.
        Assert.False(publisher.ShowsPermissionControl);
        Assert.Equal("This device", publisher.DisplayName);

        Assert.True(viewer.ShowsPermissionControl);
        Assert.True(viewer.CanTalk);
        Assert.Equal("Revoke talking", viewer.PermissionActionLabel);
        Assert.Equal(viewerId, viewer.Id);
    }

    [Fact]
    public void ARevokedParticipantIsShownAsListeningOnly()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Duplex,
            Participants = [new ParticipantAudioState(Guid.NewGuid().ToString("D"), "viewer", "duplex", false, false, true, false)],
        });

        var viewer = Assert.Single(page.Participants);
        Assert.False(viewer.CanTalk);
        Assert.Equal("Listening only", viewer.Status);
        Assert.Equal("Allow talking", viewer.PermissionActionLabel);
    }

    [Fact]
    public void TheNextSessionModeIsAUserChoiceThatSurvivesSnapshotUpdates()
    {
        var page = CreatePage();

        page.StartNextSessionAsTwoWay = true;
        page.Update(new PublisherSnapshot());

        // A session's mode is fixed at creation, so the choice has to outlive every snapshot
        // that arrives before the session exists.
        Assert.True(page.StartNextSessionAsTwoWay);
    }

    private AudioPageViewModel CreatePage()
    {
        Directory.CreateDirectory(dir);
        var store = new AudioOutputPreferenceStore(Path.Combine(dir, "audio-output.json"));
        return new AudioPageViewModel(new FakeEnumerator(), store);
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private sealed class FakeEnumerator : IAudioDeviceEnumerator
    {
        public string? PreferredDeviceId { get; private set; }
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => PreferredDeviceId = deviceId;
    }
}
