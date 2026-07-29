using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SonicRelay.Windows.Presentation.Pairing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace SonicRelay.Windows.App.Controls;

public sealed partial class PairingCard : UserControl
{
    private readonly DispatcherTimer expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairingViewModel? viewModel;
    private int imageVersion;
    private bool isLoaded;

    public PairingCard()
    {
        InitializeComponent();
        expiryTimer.Tick += OnExpiryTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Attach(PairingViewModel? value)
    {
        if (viewModel is not null)
        {
            viewModel.StateChanged -= OnStateChanged;
        }

        viewModel = value;
        if (isLoaded && viewModel is not null)
        {
            viewModel.StateChanged += OnStateChanged;
            viewModel.ClearExpiredChallenge(DateTimeOffset.UtcNow);
            _ = InitializeAsync();
            return;
        }

        _ = RenderAsync();
    }

    public void SetSessionCode(string? sessionCode)
    {
        if (viewModel is not null)
        {
            viewModel.SetSessionCode(sessionCode);
            return;
        }

        SessionCodeText.Text = string.IsNullOrWhiteSpace(sessionCode) ? "—" : sessionCode;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        expiryTimer.Stop();
        isLoaded = false;
        if (viewModel is not null)
        {
            viewModel.StateChanged -= OnStateChanged;
        }
    }

    private void OnExpiryTick(object? sender, object e) =>
        viewModel?.ClearExpiredChallenge(DateTimeOffset.UtcNow);

    private void OnStateChanged() => DispatcherQueue.TryEnqueue(() => _ = RenderAsync());

    private async Task InitializeAsync()
    {
        if (viewModel is null)
        {
            await RenderAsync();
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

    private async void RefreshChallenge_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        await RunUiOperationAsync(() => viewModel.RefreshChallengeAsync());
    }

    private async void RefreshPairings_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        await RunUiOperationAsync(() => viewModel.RefreshPairingsAsync());
    }

    private void CopyChallengeId_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(viewModel?.Challenge?.ChallengeId.ToString("D"));

    private void CopyPairingCode_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(viewModel?.Challenge?.Code);

    private static void CopyToClipboard(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void RevokePairing_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null || sender is not Button { Tag: Guid pairingId })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Revoke paired viewer?",
            Content = "This viewer will not be able to join future sessions. An already joined stream remains connected.",
            PrimaryButtonText = "Revoke",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        await RunUiOperationAsync(() => viewModel.RevokePairingAsync(pairingId, confirmed));
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
            // PairingViewModel exposes the user-facing error for RenderAsync.
        }
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        var current = viewModel;
        var challenge = current?.Challenge;
        var nextImageVersion = ++imageVersion;

        PairingCodeText.Text = challenge?.Code ?? "—";
        PairingChallengeIdText.Text = challenge?.ChallengeId.ToString("D") ?? "—";
        PairingExpiryText.Text = challenge is null
            ? "Create a pairing code to begin."
            : $"Expires {challenge.ExpiresAt.ToLocalTime():t}";
        SessionCodeText.Text = string.IsNullOrWhiteSpace(current?.SessionCode) ? "—" : current.SessionCode;
        BusyRing.IsActive = current?.IsBusy == true;
        RefreshChallengeButton.IsEnabled = current is not null && !current.IsBusy;
        RefreshPairingsButton.IsEnabled = current is not null && !current.IsBusy;
        CopyChallengeIdButton.IsEnabled = challenge is not null && current?.IsBusy != true;
        CopyPairingCodeButton.IsEnabled = challenge is not null && current?.IsBusy != true;
        RefreshChallengeButton.Content = challenge is null ? "Create pairing code" : "Refresh pairing code";
        ErrorText.Text = current?.ErrorMessage ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(current?.ErrorMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (current?.QrCodePng is not { Length: > 0 } png)
        {
            QrImage.Source = null;
        }
        else
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
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
                Text = "No active paired viewers.",
                Opacity = 0.7
            });
            return;
        }

        foreach (var pairing in current.Pairings)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new TextBlock
            {
                Text = $"Viewer {pairing.ViewerDeviceId:D}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var revoke = new Button { Content = "Revoke", Tag = pairing.PairingId };
            revoke.Click += RevokePairing_Click;
            Grid.SetColumn(revoke, 1);
            row.Children.Add(identity);
            row.Children.Add(revoke);
            PairingsPanel.Children.Add(row);
        }
    }
}
