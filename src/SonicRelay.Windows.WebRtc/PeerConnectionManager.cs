namespace SonicRelay.Windows.WebRtc;

public sealed class PeerConnectionManager : IPeerConnectionManager
{
    private readonly IWebRtcPeerConnectionFactory factory;
    private readonly WebRtcPublisherOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ManagedPeer> peers = new(StringComparer.Ordinal);
    private bool disposed;

    public PeerConnectionManager(IWebRtcPeerConnectionFactory factory, WebRtcPublisherOptions options)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public int ViewerCount
    {
        get
        {
            lock (peers) return peers.Count;
        }
    }

    public event Func<string, WebRtcIceCandidate, CancellationToken, Task>? LocalIceCandidateReady;
    public event Action? DiagnosticsChanged;

    public async Task<ViewerPeerRegistration> RegisterViewerAsync(
        string viewerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (peers.TryGetValue(viewerId, out var existing))
            {
                return new(existing.PublicPeer, false);
            }

            var connection = await factory.CreateAsync(viewerId, options, cancellationToken);
            if (!string.Equals(connection.ViewerId, viewerId, StringComparison.Ordinal))
            {
                await connection.DisposeAsync();
                throw new WebRtcPublisherException("The peer connection factory returned a mismatched viewer identity.");
            }

            Func<WebRtcIceCandidate, CancellationToken, Task> candidateHandler =
                (candidate, token) => EmitLocalCandidateAsync(viewerId, candidate, token);
            Action diagnosticsHandler = () => DiagnosticsChanged?.Invoke();
            connection.LocalIceCandidateReady += candidateHandler;
            connection.DiagnosticsChanged += diagnosticsHandler;
            var managed = new ManagedPeer(new ViewerPeer(viewerId, connection), candidateHandler, diagnosticsHandler);
            lock (peers) peers.Add(viewerId, managed);
            DiagnosticsChanged?.Invoke();
            return new(managed.PublicPeer, true);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task ApplyAnswerAsync(
        string viewerId,
        WebRtcSessionDescription answer,
        CancellationToken cancellationToken = default) =>
        GetPeer(viewerId).ApplyAnswerAsync(answer ?? throw new ArgumentNullException(nameof(answer)), cancellationToken);

    public Task<WebRtcSessionDescription?> RequestIceRestartAsync(
        string viewerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        IWebRtcPeerConnection? connection;
        lock (peers)
        {
            connection = peers.TryGetValue(viewerId, out var peer) ? peer.PublicPeer.Connection : null;
        }
        return connection is null
            ? Task.FromResult<WebRtcSessionDescription?>(null)
            : RestartAsync(connection, cancellationToken);

        static async Task<WebRtcSessionDescription?> RestartAsync(IWebRtcPeerConnection target, CancellationToken ct) =>
            await target.CreateIceRestartOfferAsync(ct);
    }

    public Task AddRemoteIceCandidateAsync(
        string viewerId,
        WebRtcIceCandidate candidate,
        CancellationToken cancellationToken = default) =>
        GetPeer(viewerId).AddRemoteIceCandidateAsync(candidate ?? throw new ArgumentNullException(nameof(candidate)), cancellationToken);

    public async Task PushAudioFrameAsync(WebRtcAudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IWebRtcPeerConnection[] snapshot;
        lock (peers) snapshot = peers.Values.Select(peer => peer.PublicPeer.Connection).ToArray();
        await Task.WhenAll(snapshot.Select(peer => peer.SendAudioFrameAsync(frame, cancellationToken)));
    }

    public async Task<bool> RemoveViewerAsync(string viewerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ManagedPeer? managed;
            lock (peers)
            {
                if (!peers.Remove(viewerId, out managed)) return false;
            }
            await DisposePeerAsync(managed);
            DiagnosticsChanged?.Invoke();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ManagedPeer[] snapshot;
            lock (peers)
            {
                snapshot = peers.Values.ToArray();
                peers.Clear();
            }
            foreach (var peer in snapshot) await DisposePeerAsync(peer);
            if (snapshot.Length > 0) DiagnosticsChanged?.Invoke();
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<PeerConnectionDiagnostics> GetDiagnostics()
    {
        lock (peers)
        {
            return peers.Values
                .Select(peer => peer.PublicPeer.Connection.Diagnostics)
                .OrderBy(item => item.ViewerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private IWebRtcPeerConnection GetPeer(string viewerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        lock (peers)
        {
            return peers.TryGetValue(viewerId, out var peer)
                ? peer.PublicPeer.Connection
                : throw new WebRtcPublisherException($"Viewer '{viewerId}' is not registered.");
        }
    }

    private async Task EmitLocalCandidateAsync(
        string viewerId,
        WebRtcIceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var handlers = LocalIceCandidateReady;
        if (handlers is null) return;
        foreach (Func<string, WebRtcIceCandidate, CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            await handler(viewerId, candidate, cancellationToken);
        }
    }

    private static async Task DisposePeerAsync(ManagedPeer managed)
    {
        managed.PublicPeer.Connection.LocalIceCandidateReady -= managed.CandidateHandler;
        managed.PublicPeer.Connection.DiagnosticsChanged -= managed.DiagnosticsHandler;
        await managed.PublicPeer.Connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await RemoveAllAsync();
        gate.Dispose();
    }

    private sealed record ManagedPeer(
        ViewerPeer PublicPeer,
        Func<WebRtcIceCandidate, CancellationToken, Task> CandidateHandler,
        Action DiagnosticsHandler);
}
