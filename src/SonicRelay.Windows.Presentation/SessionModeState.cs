using SonicRelay.Windows.ApiClient.Sessions;

namespace SonicRelay.Windows.Presentation;

/// <summary>
/// The mode of the session that is currently live, shared between the workflow that creates
/// sessions and the peer-connection factory that builds connections for them.
///
/// It exists because the two are composed at different times: the factory is constructed once,
/// before any session exists, yet each peer connection it builds must know the mode of the
/// session it belongs to — and a connection's audio direction is fixed at construction, so it
/// cannot be corrected afterwards.
/// </summary>
public sealed class SessionModeState
{
    private volatile string mode = SessionModes.Broadcast;

    public string Mode
    {
        get => mode;
        set => mode = string.IsNullOrWhiteSpace(value) ? SessionModes.Broadcast : value;
    }

    public bool IsDuplex => SessionModes.IsDuplex(mode);
}
