using System.Net.WebSockets;
using System.Threading.Channels;
using SonicRelay.Windows.Signaling.WebSockets;

namespace SonicRelay.Windows.Signaling.Tests;

internal sealed class RecordingHandler : ISignalingMessageHandler
{
    private readonly Channel<SignalingMessageEnvelope> messages = Channel.CreateUnbounded<SignalingMessageEnvelope>();

    public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        messages.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public Task<SignalingMessageEnvelope> NextAsync(CancellationToken cancellationToken) =>
        messages.Reader.ReadAsync(cancellationToken).AsTask();
}

internal sealed class FakeWebSocketFactory(params FakeWebSocketConnection[] connections) : IWebSocketConnectionFactory
{
    private readonly Queue<FakeWebSocketConnection> remaining = new(connections);
    public int CreatedCount { get; private set; }

    public IWebSocketConnection Create()
    {
        CreatedCount++;
        return remaining.Dequeue();
    }
}

internal sealed class FakeWebSocketConnection : IWebSocketConnection
{
    private readonly Channel<WebSocketInboundMessage> inbound = Channel.CreateUnbounded<WebSocketInboundMessage>();

    private readonly TaskCompletionSource<WebSocketInboundMessage> receiveFault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri? ConnectedUri { get; private set; }
    public string? AccessToken { get; private set; }
    public List<string> Sent { get; } = [];
    public WebSocketState State { get; private set; } = WebSocketState.None;
    public bool Disposed { get; private set; }
    public Exception? ConnectException { get; init; }

    /// <summary>
    /// Reproduces how a real <see cref="System.Net.WebSockets.ClientWebSocket"/> behaves when it
    /// is torn down while a receive is pending: it is the abort — not the cancellation token —
    /// that completes the pending read, and it completes it with a transient
    /// <see cref="WebSocketException"/> rather than an <see cref="OperationCanceledException"/>.
    /// The plain fake resolves the read from the token instead, which is why it could never
    /// surface the close-vs-reconnect race.
    /// </summary>
    public bool FaultPendingReceiveOnClose { get; init; }

    public Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
    {
        if (ConnectException is not null)
        {
            return Task.FromException(ConnectException);
        }
        ConnectedUri = uri;
        AccessToken = accessToken;
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public async Task<WebSocketInboundMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (!FaultPendingReceiveOnClose)
        {
            return await inbound.Reader.ReadAsync(cancellationToken);
        }

        // Deliberately ignores the token: only the close may complete this read, so the test
        // observes the WebSocketException path rather than racing it against a cancellation.
        var read = inbound.Reader.ReadAsync(CancellationToken.None).AsTask();
        return await await Task.WhenAny(read, receiveFault.Task);
    }

    public Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        FaultPendingReceive();
        return Task.CompletedTask;
    }

    public void QueueText(SignalingMessageEnvelope message) =>
        inbound.Writer.TryWrite(new WebSocketInboundMessage(WebSocketMessageType.Text, message.Serialize(), null));

    public void QueueText(string message) =>
        inbound.Writer.TryWrite(new WebSocketInboundMessage(WebSocketMessageType.Text, message, null));

    public void QueueClose(WebSocketCloseStatus? status = WebSocketCloseStatus.NormalClosure) =>
        inbound.Writer.TryWrite(new WebSocketInboundMessage(WebSocketMessageType.Close, null, status));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        State = WebSocketState.Closed;
        FaultPendingReceive();
        return ValueTask.CompletedTask;
    }

    private void FaultPendingReceive()
    {
        if (!FaultPendingReceiveOnClose) return;
        receiveFault.TrySetException(
            new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The socket was torn down."));
    }
}

internal sealed class ThrowingHandler : ISignalingMessageHandler
{
    public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("handler boom");
}

internal sealed class ImmediateReconnectDelay : IReconnectDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="ImmediateReconnectDelay"/> that honours cancellation the way the production
/// <c>Task.Delay</c>-backed delay does. The immediate double swallows an already-cancelled token,
/// so a reconnect loop entered with a cancelled lifecycle looks healthy under test while it
/// aborts on the very first delay in production.
/// </summary>
internal sealed class CancellationAwareReconnectDelay : IReconnectDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
