using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Minimal observable base. The shell keeps a deliberately small MVVM surface (plain
/// <see cref="INotifyPropertyChanged"/>, no external toolkit) so the shared view-model
/// layer carries no UI-framework dependency it would have to shed for Linux (issue #32).
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets <paramref name="field"/> and raises change notification when the value differs.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }
}
