namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>The shell's navigable destinations (issue #32).</summary>
public enum PageKey { Dashboard, Audio, Session, Diagnostics, Settings }

/// <summary>
/// A sidebar navigation entry. The Dashboard, Session and Diagnostics surfaces are live;
/// Audio and Settings are declared but disabled placeholders for later slices (they need
/// device enumeration and the preference stores — issue #32).
/// </summary>
public sealed class NavigationItem : ViewModelBase
{
    private bool isEnabled = true;

    public NavigationItem(PageKey key, string glyph, string label)
    {
        Key = key;
        Glyph = glyph;
        Label = label;
    }

    public PageKey Key { get; }

    /// <summary>An emoji/text glyph; the shell avoids an icon-font dependency in this phase.</summary>
    public string Glyph { get; }
    public string Label { get; }

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }
}
