using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SonicRelay.Windows.Presentation;

namespace SonicRelay.Windows.App.Pages;

public sealed partial class ConnectionPage : Page
{
    private PublisherWorkflow? workflow;

    public ConnectionPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.CurrentApp.RuntimeChanged += OnRuntimeChanged;
        Attach(App.CurrentApp.Runtime);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.CurrentApp.RuntimeChanged -= OnRuntimeChanged;
        Attach(null);
    }

    private void OnRuntimeChanged(PublisherRuntime? runtime) =>
        DispatcherQueue.TryEnqueue(() => Attach(runtime));

    private void Attach(PublisherRuntime? runtime)
    {
        if (workflow is not null)
        {
            workflow.StateChanged -= OnStateChanged;
        }

        workflow = runtime?.Workflow;
        if (workflow is not null)
        {
            workflow.StateChanged += OnStateChanged;
        }

        PairingCard.Attach(runtime?.Pairing);
        BackendText.Text = runtime?.BackendBaseUrl.AbsoluteUri ?? "Backend not configured";
        Render(workflow?.State);
    }

    private void OnStateChanged(PublisherSnapshot state) =>
        DispatcherQueue.TryEnqueue(() => Render(state));

    private void Render(PublisherSnapshot? state)
    {
        PairingCard.SetSessionCode(state?.SessionCode);
        DeviceStatusText.Text = state?.HasDeviceIdentity == true
            ? "Publisher device identity ready"
            : "Publisher device identity unavailable";
        BusyRing.IsActive = state?.IsBusy == true;
        ErrorBar.Message = state?.ErrorMessage ?? string.Empty;
        ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(state?.ErrorMessage);
    }
}
