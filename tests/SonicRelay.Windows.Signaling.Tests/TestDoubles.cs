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

    public Uri? ConnectedUri { get; private set; }
    public string? AccessToken { get; private set; }
    public List<string> Sent { get; } = [];
    public WebSocketState State { get; private set; } = WebSocketState.None;
    public bool Disposed { get; private set; }
    public Exception? ConnectException { get; init; }

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

    public Task<WebSocketInboundMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        inbound.Reader.ReadAsync(cancellationToken).AsTask();

    public Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
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
        return ValueTask.CompletedTask;
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
