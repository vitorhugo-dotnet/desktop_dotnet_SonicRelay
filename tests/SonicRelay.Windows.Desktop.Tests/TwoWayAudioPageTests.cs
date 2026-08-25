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
        Assert.Equal("Stop sending audio", page.MuteActionLabel);
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
        Assert.Equal("Resume sending audio", page.MuteActionLabel);
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
        Assert.Equal("Stop accepting audio", viewer.PermissionActionLabel);
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
        Assert.Equal("Accept audio", viewer.PermissionActionLabel);
    }

    [Fact]
    public void PlayingOntoTheCapturedOutputIsSurfacedAsALoop()
    {
        var page = CreatePage();
        var device = new AudioDeviceInfo("render-1", "Speakers", 48000, 2, AudioSampleFormat.IeeeFloat32);

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Duplex,
            PlaybackDevice = device,
            AudioDiagnostics = new AudioCaptureDiagnostics(
                AudioCaptureState.Capturing, device, null, AudioLevelSnapshot.Silence, 0, 0),
        });

        // Capturing the same endpoint that incoming audio plays on feeds the other side its
        // own audio back. It cannot be fixed from inside the app, so it is surfaced.
        Assert.True(page.PlaysIntoCapturedOutput);
    }

    [Fact]
    public void DistinctCaptureAndPlaybackEndpointsAreNotALoop()
    {
        var page = CreatePage();

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Duplex,
            PlaybackDevice = new AudioDeviceInfo("headset", "Headset", 48000, 2, AudioSampleFormat.IeeeFloat32),
            AudioDiagnostics = new AudioCaptureDiagnostics(
                AudioCaptureState.Capturing,
                new AudioDeviceInfo("render-1", "Speakers", 48000, 2, AudioSampleFormat.IeeeFloat32),
                null,
                AudioLevelSnapshot.Silence,
                0,
                0),
        });

        Assert.False(page.PlaysIntoCapturedOutput);
    }

    [Fact]
    public void AOneWaySessionNeverReportsALoop()
    {
        var page = CreatePage();
        var device = new AudioDeviceInfo("render-1", "Speakers", 48000, 2, AudioSampleFormat.IeeeFloat32);

        page.Update(new PublisherSnapshot
        {
            SessionId = Guid.NewGuid(),
            SessionMode = SessionModes.Broadcast,
            PlaybackDevice = device,
            AudioDiagnostics = new AudioCaptureDiagnostics(
                AudioCaptureState.Capturing, device, null, AudioLevelSnapshot.Silence, 0, 0),
        });

        // Nothing is played back in a one-way session, so the endpoints matching is harmless.
        Assert.False(page.PlaysIntoCapturedOutput);
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
