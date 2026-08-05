namespace SonicRelay.Windows.Core.Configuration;

/// <summary>The three mutually-exclusive relay policies, matching the backend's RelayModes
/// (dotnet_SonicRelay's SonicRelay.Domain.RelaySettings.RelayModes) string-for-string so the
/// value round-trips through /api/settings/relay unchanged.</summary>
public static class RelayModes
{
    public const string Automatic = "automatic";
    public const string ForceRelay = "forceRelay";
    public const string DisableFallback = "disableFallback";

    public static bool IsValid(string? value) => value is Automatic or ForceRelay or DisableFallback;
}
