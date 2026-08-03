using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// The publisher's pairing surface (issue #26): displays the current pairing challenge (QR
/// code + manual code) and the list of paired viewers, driven entirely by a
/// <see cref="PairingViewModel"/> DataContext. Ported from the WinUI-era PairingCard control
/// (retired when the Avalonia shell replaced SonicRelay.Windows.App) — this is deliberately a
/// thin, code-behind-driven view like that original: <see cref="PairingViewModel"/> is a plain
/// class with a <see cref="PairingViewModel.StateChanged"/> event rather than
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/>, so it renders imperatively
/// instead of through compiled bindings.
/// </summary>
public partial class PairingView : UserControl
{
    private readonly DispatcherTimer expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairingViewModel? viewModel;
    private int imageVersion;
    private bool isLoaded;

    public PairingView()
    {
        InitializeComponent();
        expiryTimer.Tick += OnExpiryTick;
        DataContextChanged += (_, _) => Attach(DataContext as PairingViewModel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void Attach(PairingViewModel? value)
    {
        if (ReferenceEquals(viewModel, value)) return;

        if (viewModel is not null)
        {
            viewModel.StateChanged -= OnStateChanged;
        }

        viewModel = value;
        if (!isLoaded)
        {
            // The named XAML elements Render() touches are only guaranteed to exist once
            // this control has actually loaded — production crashed here with a
            // NullReferenceException on DeviceStatusText because device-identity bootstrap
            // can attach a real PairingViewModel before the shell's first layout pass
            // completes. OnLoaded picks up the pending viewModel and renders once it's safe.
            return;
        }

        if (viewModel is not null)
        {
            viewModel.StateChanged += OnStateChanged;
            viewModel.ClearExpiredChallenge(DateTimeOffset.UtcNow);
            _ = InitializeAsync();
            return;
        }

        Render();
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        isLoaded = true;
        if (viewModel is not null)
        {
            viewModel.StateChanged -= OnStateChanged;
            viewModel.ClearExpiredChallenge(DateTimeOffset.UtcNow);
            viewModel.StateChanged += OnStateChanged;
        }
        expiryTimer.Start();
        _ = InitializeAsync();
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        expiryTimer.Stop();
        isLoaded = false;
        if (viewModel is not null)
        {
            viewModel.StateChanged -= OnStateChanged;
        }
    }

    private void OnExpiryTick(object? sender, EventArgs e) =>
        viewModel?.ClearExpiredChallenge(DateTimeOffset.UtcNow);

    private void OnStateChanged() => Dispatcher.UIThread.Post(Render);

    private async Task InitializeAsync()
    {
        if (viewModel is null)
        {
            Render();
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            if (viewModel.Challenge is null)
            {
                await viewModel.RefreshChallengeAsync();
            }
            await viewModel.RefreshPairingsAsync();
        });
    }

    private async void RefreshChallenge_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (viewModel is null) return;
        await RunUiOperationAsync(() => viewModel.RefreshChallengeAsync());
    }

    private async void RefreshPairings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (viewModel is null) return;
        await RunUiOperationAsync(() => viewModel.RefreshPairingsAsync());
    }

    private async void CopyChallengeId_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await CopyToClipboardAsync(viewModel?.Challenge?.ChallengeId.ToString("D"));

    private async void CopyPairingCode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await CopyToClipboardAsync(viewModel?.Challenge?.Code);

    private async Task CopyToClipboardAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(value);
        }
    }

    // Revoking a pairing is reversible (the viewer can simply be paired again), so this
    // skips a confirmation dialog rather than depend on a modal-dialog package Avalonia
    // does not ship in core.
    private async void RevokePairing_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (viewModel is null || sender is not Button { Tag: Guid pairingId }) return;
        await RunUiOperationAsync(() => viewModel.RevokePairingAsync(pairingId, confirmed: true));
    }

    private async Task RunUiOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // PairingViewModel exposes the user-facing error via ErrorMessage for Render.
        }
        Render();
    }

    private void Render()
    {
        var current = viewModel;
        var challenge = current?.Challenge;
        var nextImageVersion = ++imageVersion;

        DeviceStatusText.Text = current is null
            ? "Publisher device identity unavailable"
            : "Publisher device identity ready";
        PairingCodeText.Text = challenge?.Code ?? "—";
        PairingChallengeIdText.Text = challenge?.ChallengeId.ToString("D") ?? "—";
        PairingExpiryText.Text = challenge is null
            ? "Create a pairing code to begin."
            : $"Expires {challenge.ExpiresAt.ToLocalTime():t}";
        SessionCodeText.Text = string.IsNullOrWhiteSpace(current?.SessionCode) ? "—" : current.SessionCode;

        var busy = current?.IsBusy == true;
        BusyText.IsVisible = busy;
        RefreshChallengeButton.IsEnabled = current is not null && !busy;
        RefreshPairingsButton.IsEnabled = current is not null && !busy;
        CopyChallengeIdButton.IsEnabled = challenge is not null && !busy;
        CopyPairingCodeButton.IsEnabled = challenge is not null && !busy;
        RefreshChallengeButton.Content = challenge is null ? "Create pairing code" : "Refresh pairing code";

        ErrorText.Text = current?.ErrorMessage ?? string.Empty;
        ErrorText.IsVisible = !string.IsNullOrWhiteSpace(current?.ErrorMessage);

        if (current?.QrCodePng is not { Length: > 0 } png)
        {
            QrImage.Source = null;
        }
        else
        {
            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);
            if (nextImageVersion == imageVersion)
            {
                QrImage.Source = bitmap;
            }
        }

        RenderPairings(current);
    }

    private void RenderPairings(PairingViewModel? current)
    {
        PairingsPanel.Children.Clear();
        if (current is null || current.Pairings.Count == 0)
        {
            PairingsPanel.Children.Add(new TextBlock
            {
                Classes = { "metric-label" },
                Text = "No active paired viewers.",
            });
            return;
        }

        foreach (var pairing in current.Pairings)
        {
            PairingsPanel.Children.Add(BuildPairingRow(pairing));
        }
    }

    private Control BuildPairingRow(PairingResponse pairing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var identity = new TextBlock
        {
            Classes = { "metric-label" },
            Text = $"Viewer {pairing.ViewerDeviceId:D}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var revoke = new Button
        {
            Classes = { "ghost" },
            Content = "Revoke",
            Tag = pairing.PairingId,
        };
        revoke.Click += RevokePairing_Click;
        Grid.SetColumn(revoke, 1);
        row.Children.Add(identity);
        row.Children.Add(revoke);
        return row;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
