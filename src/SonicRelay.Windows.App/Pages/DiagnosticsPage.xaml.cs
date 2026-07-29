using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private PublisherRuntime? runtime;
    private PublisherWorkflow? workflow;
    private DiagnosticsSnapshot? snapshot;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.CurrentApp.RuntimeChanged += RuntimeChanged;
        Attach(App.CurrentApp.Runtime);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.CurrentApp.RuntimeChanged -= RuntimeChanged;
        Attach(null);
    }

    private void RuntimeChanged(PublisherRuntime? next) => DispatcherQueue.TryEnqueue(() => Attach(next));

    private void Attach(PublisherRuntime? next)
    {
        if (workflow is not null) workflow.StateChanged -= StateChanged;
        if (runtime is not null) runtime.WebRtcPublisher.DiagnosticsChanged -= WebRtcDiagnosticsChanged;
        runtime = next;
        workflow = runtime?.Workflow;
        if (workflow is not null) workflow.StateChanged += StateChanged;
        if (runtime is not null) runtime.WebRtcPublisher.DiagnosticsChanged += WebRtcDiagnosticsChanged;
        Render(workflow?.State);
    }

    private void StateChanged(PublisherSnapshot state) => DispatcherQueue.TryEnqueue(() => Render(state));

    // WebRTC peer state (ICE connect/disconnect) changes independently of the
    // workflow snapshot, so refresh the diagnostics view on those too.
    private void WebRtcDiagnosticsChanged(SonicRelay.Windows.WebRtc.WebRtcPublisherDiagnostics _) =>
        DispatcherQueue.TryEnqueue(() => Render(workflow?.State));

    private void Render(PublisherSnapshot? state)
    {
        snapshot = CreateSnapshot(state);
        AppVersionText.Text = snapshot.AppVersion;
        RuntimeVersionText.Text = snapshot.RuntimeVersion;
        OsVersionText.Text = snapshot.OsVersion;
        BackendText.Text = snapshot.Backend.Host;
        AuthText.Text = snapshot.Auth.IsAuthenticated ? "DeviceBearer ready" : "Unavailable";
        DeviceText.Text = snapshot.Device.MaskedId;
        SessionText.Text = snapshot.Session.MaskedId;
        SignalingText.Text = snapshot.Signaling.ConnectionState;
        ViewerText.Text = $"{snapshot.Session.ViewerCount} / {snapshot.WebRtc.PeerCount}";
        AudioStateText.Text = snapshot.AudioCapture.CaptureState;
        AudioDeviceText.Text = snapshot.AudioCapture.OutputDevice;
        AudioLevel.Value = snapshot.AudioCapture.Level * 100;
        ErrorList.ItemsSource = snapshot.LastErrors;
        LogList.ItemsSource = state?.ActivityLog.Select(DiagnosticRedactor.Redact).Reverse().ToArray() ?? [];
    }

    private DiagnosticsSnapshot CreateSnapshot(PublisherSnapshot? state)
    {
        var audio = state?.AudioDiagnostics;
        var errors = state?.ActivityLog
            .Where(item => item.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            .Select(DiagnosticRedactor.Redact)
            .TakeLast(20)
            .ToList() ?? [];
        if (audio?.LastError is { } audioError) errors.Add(DiagnosticRedactor.Redact(audioError.Message));
        var signalingStatus = state?.SignalingState == SignalingConnectionState.Connected
            ? DiagnosticStatus.Healthy : DiagnosticStatus.Unavailable;
        var webRtc = runtime?.WebRtcPublisher.Diagnostics;
        if (webRtc?.LastError is { } webRtcError) errors.Add(DiagnosticRedactor.Redact(webRtcError));
        return new DiagnosticsSnapshot(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Environment.Version.ToString(),
            Environment.OSVersion.VersionString,
            new(runtime is null ? DiagnosticStatus.Unavailable : DiagnosticStatus.Healthy, DiagnosticRedactor.BackendHost(runtime?.BackendBaseUrl)),
            new(state?.IsAuthenticated == true ? DiagnosticStatus.Healthy : DiagnosticStatus.Unavailable, state?.IsAuthenticated == true),
            new(state?.DeviceId is null ? DiagnosticStatus.Unavailable : DiagnosticStatus.Healthy, DiagnosticRedactor.MaskIdentifier(state?.DeviceId?.ToString("D"))),
            new(state?.SessionId is null ? DiagnosticStatus.Unavailable : DiagnosticStatus.Healthy, DiagnosticRedactor.MaskIdentifier(state?.SessionId?.ToString("D")), state?.ViewerCount ?? 0),
            new(signalingStatus, state?.SignalingState.ToString() ?? "Not configured"),
            new(audio?.LastError is null ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded, state?.AudioState.ToString() ?? "Not configured", audio?.Device?.Name ?? "No output device selected", audio?.Level.Peak ?? 0),
            new(WebRtcStatus(webRtc), webRtc?.ViewerConnectionCount ?? 0),
            errors);
    }

    private static DiagnosticStatus WebRtcStatus(SonicRelay.Windows.WebRtc.WebRtcPublisherDiagnostics? webRtc)
    {
        if (webRtc is null || webRtc.ViewerConnectionCount == 0) return DiagnosticStatus.Unknown;
        var anyConnected = webRtc.Viewers.Any(viewer =>
            viewer.State == SonicRelay.Windows.WebRtc.PeerConnectionState.Connected);
        return anyConnected ? DiagnosticStatus.Healthy : DiagnosticStatus.Degraded;
    }

    private async void ExportReport(object sender, RoutedEventArgs e)
    {
        if (runtime is null || snapshot is null)
        {
            ExportStatus.Text = "Configure the backend before exporting diagnostics.";
            return;
        }

        try
        {
            ExportStatus.Text = $"Safe report exported to {await runtime.ReportExporter.ExportAsync(snapshot)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExportStatus.Text = $"Export failed: {DiagnosticRedactor.Redact(exception.Message)}";
        }
    }
}
