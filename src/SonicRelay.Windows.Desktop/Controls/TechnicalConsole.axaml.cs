using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// Terminal-style event log (issue #32 component). Renders the publisher activity log from
/// the shell view model and keeps the newest line in view.
/// </summary>
public partial class TechnicalConsole : UserControl
{
    private DashboardShellViewModel? observed;

    public TechnicalConsole()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (observed is not null) observed.PropertyChanged -= OnShellPropertyChanged;
        observed = DataContext as DashboardShellViewModel;
        if (observed is not null) observed.PropertyChanged += OnShellPropertyChanged;
        ScrollToEnd();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardShellViewModel.ActivityLog))
            ScrollToEnd();
    }

    private void ScrollToEnd() =>
        // Resolved from the name scope rather than a generated field: the log can change
        // before the field is assigned, and the ScrollViewer must exist to scroll anyway.
        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(),
            DispatcherPriority.Background);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
