namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>The shell's navigable destinations (issue #32).</summary>
public enum PageKey { Dashboard, Pairing, Audio, Session, Diagnostics, Settings }

/// <summary>
/// A sidebar navigation entry. All destinations — Dashboard, Pairing, Audio, Session,
/// Diagnostics and Settings — are live and always enabled; Pairing in particular is an
/// ordinary, always-reachable nav page rather than a full-shell gate (issue #26 follow-up).
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
