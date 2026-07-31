using SonicRelay.Windows.Desktop.ViewModels;

namespace SonicRelay.Windows.Desktop.Tests;

/// <summary>
/// The auth form is a thin forwarder: it collects credentials and dispatches to the
/// login/register delegates, while validation and error text stay with the workflow (#32).
/// </summary>
public sealed class AuthViewModelTests
{
    private static AuthViewModel Build(
        out List<(string Email, string Password)> logins,
        out List<(string Email, string Password, string Confirm)> registrations)
    {
        var l = new List<(string, string)>();
        var r = new List<(string, string, string)>();
        logins = l;
        registrations = r;
        return new AuthViewModel(
            (email, password) => { l.Add((email, password)); return Task.CompletedTask; },
            (email, password, confirm) => { r.Add((email, password, confirm)); return Task.CompletedTask; });
    }

    [Fact]
    public void Submit_in_login_mode_dispatches_to_the_login_delegate()
    {
        var vm = Build(out var logins, out var registrations);
        vm.Email = "user@example.com";
        vm.Password = "secret";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(("user@example.com", "secret"), Assert.Single(logins));
        Assert.Empty(registrations);
    }

    [Fact]
    public void Submit_in_register_mode_dispatches_to_the_register_delegate()
    {
        var vm = Build(out var logins, out var registrations);
        vm.IsRegisterMode = true;
        vm.Email = "user@example.com";
        vm.Password = "secret";
        vm.ConfirmPassword = "secret";

        vm.SubmitCommand.Execute(null);

        Assert.Equal(("user@example.com", "secret", "secret"), Assert.Single(registrations));
        Assert.Empty(logins);
    }

    [Fact]
    public void Submit_is_disabled_until_the_required_fields_are_filled()
    {
        var vm = Build(out _, out _);
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.Email = "user@example.com";
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.Password = "secret";
        Assert.True(vm.SubmitCommand.CanExecute(null));

        // Register mode also needs the confirmation field.
        vm.IsRegisterMode = true;
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.ConfirmPassword = "secret";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Busy_disables_submit()
    {
        var vm = Build(out _, out _);
        vm.Email = "user@example.com";
        vm.Password = "secret";
        Assert.True(vm.SubmitCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Toggle_switches_labels_between_sign_in_and_register()
    {
        var vm = Build(out _, out _);
        Assert.Equal("Sign in", vm.SubmitText);

        vm.ToggleModeCommand.Execute(null);

        Assert.True(vm.IsRegisterMode);
        Assert.Equal("Create account", vm.SubmitText);
    }

    [Fact]
    public void Error_message_drives_has_error()
    {
        var vm = Build(out _, out _);
        Assert.False(vm.HasError);

        vm.ErrorMessage = "Login failed. Check your email and password.";

        Assert.True(vm.HasError);
    }
}
