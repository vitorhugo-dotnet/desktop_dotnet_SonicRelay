using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Pure gating for the shell's contextual actions. Create/start/stop/end defer to the
/// <see cref="PublisherSnapshot"/>'s own action guards — the same conditions
/// <see cref="PublisherWorkflow"/> enforces — rather than the coarser, state-derived
/// <see cref="PublisherUiCapabilities"/>. That difference matters: a session can be capturing
/// while still in <see cref="PublisherUiState.WaitingViewer"/> (audio started before a viewer
/// connected), where the capability set reports <c>CanStopAudio: false</c> even though the
/// snapshot correctly reports it <c>true</c>. Retry and logout have no snapshot guard, so they
/// stay capability-based (plus a not-busy check). All actions require an attached workflow.
/// </summary>
public static class ShellCommandAvailability
{
    public static bool CreateSession(PublisherSnapshot? snapshot, bool hasWorkflow) =>
        hasWorkflow && snapshot?.CanCreateSession == true;

    public static bool StartAudio(PublisherSnapshot? snapshot, bool hasWorkflow) =>
        hasWorkflow && snapshot?.CanStartAudio == true;

    public static bool StopAudio(PublisherSnapshot? snapshot, bool hasWorkflow) =>
        hasWorkflow && snapshot?.CanStopAudio == true;

    public static bool EndSession(PublisherSnapshot? snapshot, bool hasWorkflow) =>
        hasWorkflow && snapshot?.CanEndSession == true;

    public static bool Retry(PublisherSnapshot? snapshot, PublisherUiCapabilities capabilities, bool hasWorkflow) =>
        hasWorkflow && snapshot?.IsBusy != true && capabilities.CanRetry;

    public static bool Logout(PublisherSnapshot? snapshot, PublisherUiCapabilities capabilities, bool hasWorkflow) =>
        hasWorkflow && snapshot?.IsBusy != true && capabilities.CanLogout;
}
