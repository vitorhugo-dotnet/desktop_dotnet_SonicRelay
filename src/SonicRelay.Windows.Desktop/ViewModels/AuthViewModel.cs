namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Sign-in / registration form for the shell. It is deliberately thin: field state and the
/// sign-in-vs-register toggle live here, but the actual credential validation and error
/// messages come from <see cref="PublisherWorkflow"/> (surfaced through the snapshot and
/// pushed back in via <see cref="ErrorMessage"/> / <see cref="IsBusy"/>). Submitting forwards
/// to the login/register delegates the host supplies, so this view model has no API or runtime
/// dependency and stays unit-testable.
/// </summary>
public sealed class AuthViewModel : ViewModelBase
{
    private readonly Func<string, string, Task> login;
    private readonly Func<string, string, string, Task> register;
    private string email = string.Empty;
    private string password = string.Empty;
    private string confirmPassword = string.Empty;
    private bool isRegisterMode;
    private bool isBusy;
    private string? errorMessage;

    public AuthViewModel(Func<string, string, Task> login, Func<string, string, string, Task> register)
    {
        this.login = login ?? throw new ArgumentNullException(nameof(login));
        this.register = register ?? throw new ArgumentNullException(nameof(register));
        SubmitCommand = new RelayCommand(SubmitAsync, CanSubmit);
        ToggleModeCommand = new RelayCommand(() => { IsRegisterMode = !IsRegisterMode; return Task.CompletedTask; });
    }

    public RelayCommand SubmitCommand { get; }
    public RelayCommand ToggleModeCommand { get; }

    public string Email
    {
        get => email;
        set { if (SetProperty(ref email, value)) SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public string Password
    {
        get => password;
        set { if (SetProperty(ref password, value)) SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public string ConfirmPassword
    {
        get => confirmPassword;
        set { if (SetProperty(ref confirmPassword, value)) SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsRegisterMode
    {
        get => isRegisterMode;
        set
        {
            if (!SetProperty(ref isRegisterMode, value)) return;
            RaisePropertyChanged(nameof(SubmitText));
            RaisePropertyChanged(nameof(ToggleText));
            RaisePropertyChanged(nameof(Title));
            SubmitCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        set { if (SetProperty(ref isBusy, value)) SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        set
        {
            if (SetProperty(ref errorMessage, value))
                RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(errorMessage);

    public string Title => isRegisterMode ? "Create your account" : "Sign in to SonicRelay";
    public string SubmitText => isRegisterMode ? "Create account" : "Sign in";
    public string ToggleText => isRegisterMode ? "Already have an account? Sign in" : "Need an account? Register";

    private bool CanSubmit()
    {
        if (isBusy || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return false;
        return !isRegisterMode || !string.IsNullOrWhiteSpace(confirmPassword);
    }

    private Task SubmitAsync() => isRegisterMode
        ? register(email, password, confirmPassword)
        : login(email, password);
}
