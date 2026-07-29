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

    private void OnRuntimeChanged(PublisherRuntime? runtime) => DispatcherQueue.TryEnqueue(() => Attach(runtime));

    private void Attach(PublisherRuntime? runtime)
    {
        if (workflow is not null) workflow.StateChanged -= OnStateChanged;
        workflow = runtime?.Workflow;
        if (workflow is not null) workflow.StateChanged += OnStateChanged;
        // Reflect the backend actually configured (persisted across launches) so a
        // returning user signs in against their server, not the XAML placeholder.
        // The first-run template points at localhost — keep the placeholder then.
        if (runtime is not null && !runtime.BackendBaseUrl.IsLoopback)
        {
            BackendUrlBox.Text = runtime.BackendBaseUrl.AbsoluteUri;
        }
        Render(workflow?.State);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        if (!TryGetBackend(out var backend)) return;
        try
        {
            // Skip the automatic session restore: this click signs in explicitly,
            // and racing the two used to surface confusing auth-state errors.
            await App.CurrentApp.ConfigureBackendAsync(backend, restoreSession: false);
            await App.CurrentApp.Runtime!.Workflow.LoginAsync(EmailBox.Text, PasswordBox.Password);
            PasswordBox.Password = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        if (!TryGetBackend(out var backend)) return;

        var emailBox = new TextBox { Header = "Email", Text = EmailBox.Text ?? string.Empty };
        var passwordBox = new PasswordBox { Header = "Password" };
        var confirmBox = new PasswordBox { Header = "Confirm password" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(emailBox);
        content.Children.Add(passwordBox);
        content.Children.Add(confirmBox);

        var dialog = new ContentDialog
        {
            Title = "Create account",
            PrimaryButtonText = "Create account",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = content
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await App.CurrentApp.ConfigureBackendAsync(backend, restoreSession: false);
            await App.CurrentApp.Runtime!.Workflow.RegisterAsync(
                emailBox.Text, passwordBox.Password, confirmBox.Password);
            EmailBox.Text = emailBox.Text;
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            // Never keep the entered secrets around after the dialog closes.
            passwordBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        var current = App.CurrentApp.Runtime?.Workflow;
        if (current is null) return;
        try
        {
            await current.LogoutAsync();
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        var current = App.CurrentApp.Runtime?.Workflow;
        if (current is null) return;

        var dialog = new ContentDialog
        {
            Title = "Delete account?",
            Content = "This permanently disables your SonicRelay account. Your devices and "
                + "active sessions are revoked and you will be signed out. This cannot be undone.",
            PrimaryButtonText = "Delete account",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await current.DeleteAccountAsync();
        }
        catch (Exception exception)
        {
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private bool TryGetBackend(out Uri backend)
    {
        if (Uri.TryCreate(BackendUrlBox.Text?.Trim(), UriKind.Absolute, out backend!)
            && backend.Scheme is "http" or "https")
        {
            return true;
        }
        ErrorBar.Message = "Backend URL is required and must use HTTP or HTTPS.";
        ErrorBar.IsOpen = true;
        return false;
    }

    private void OnStateChanged(PublisherSnapshot state) => DispatcherQueue.TryEnqueue(() => Render(state));

    private void Render(PublisherSnapshot? state)
    {
        PairingCard.SetSessionCode(state?.SessionCode);
        var authenticated = state?.IsAuthenticated == true;
        SignInPanel.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        AccountPanel.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        AccountEmailText.Text = state?.UserEmail ?? state?.UserDisplayName ?? "Signed in";
        BusyRing.IsActive = state?.IsBusy == true;
        LoginButton.IsEnabled = state?.IsBusy != true;
        CreateAccountButton.IsEnabled = state?.IsBusy != true;
        LogoutButton.IsEnabled = state?.IsBusy != true;
        DeleteAccountButton.IsEnabled = state?.IsBusy != true;
        ErrorBar.Message = state?.ErrorMessage ?? string.Empty;
        ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(state?.ErrorMessage);
    }
}
