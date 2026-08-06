namespace SonicRelay.Windows.Core.Configuration;

/// <summary>The three mutually-exclusive relay policies, matching the backend's and the
/// Android/iOS clients' own copies of this same set string-for-string, since the literal is
/// shared across all three repos in this project.</summary>
public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string? value) => value is Automatic or ForceRelay or DisableFallback;
}
